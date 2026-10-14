using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using MLAstroRPA.Services;
using MLAstroRPA.Settings;
using NINA.Core.Utility;

namespace MLAstroRPA.Broker
{
    /// <summary>
    /// Runs the correction loop for one TPPA session: receives a measurement, decides the move, holds
    /// the capture window, drives the hardware and asks TPPA for the next measurement. It never ends a
    /// session because of the measured error alone - TPPA owns the session lifecycle and only asks for
    /// a move or reports state.
    /// </summary>
    public sealed class HardwareBusyConfirmationRequest : EventArgs
    {
        public HardwareBusyConfirmationRequest(string hardwareStatus, string linkPath)
        {
            HardwareStatus = hardwareStatus;
            LinkPath = linkPath;
        }

        /// <summary>Firmware STATUS token of the axis state that is not idle.</summary>
        public string HardwareStatus { get; }

        /// <summary>Link the axes are reached over, for the dialog text.</summary>
        public string LinkPath { get; }

        /// <summary>Set by the UI side when the operator confirmed that the travel may be stopped.</summary>
        public bool Confirmed { get; set; }
    }

    public sealed class BridgeRunner : IDisposable
    {
        /// <summary>How long we wait for TPPA to grant a capture window after asking for it.</summary>
        private const int WindowGrantTimeoutMs = 15000;

        /// <summary>
        /// How long a pause waits for the axes to really stand still after it sent the STOP, because the firmware
        /// reports READY while the mechanics still coast. A longer wait is no help - the pause has to end either way.
        /// </summary>
        private static readonly TimeSpan PauseSettleTimeout = TimeSpan.FromSeconds(5);

        /// <summary>Number of unusable (unstable / failed) measurements before the session is cancelled.</summary>
        private const int MaxUnusableMeasurements = 5;

        /// <summary>
        /// How many consecutive measurements may come back worse on one axis before the direction of that axis is
        /// declared wrong - the plugin then changes the direction itself or cancels the session.
        /// </summary>
        private const int MaxWorseningMeasurements = 2;

        /// <summary>
        /// Consecutive growing measurements an axis whose direction is already confirmed may still show before the
        /// session is stopped. The earlier ones only warn and the correction goes on: a direction that an improvement
        /// has proven cannot turn wrong later, so growth that late is play, backlash, a slipping coupling or a
        /// measurement problem - the direction setting must not be blamed for it.
        /// </summary>
        private const int MaxConfirmedGrowthMeasurements = 3;

        /// <summary>
        /// Consecutive solves inside the tolerance that TPPA has to report before this controller asks it
        /// to finish. TPPA counts them itself; this is only the fallback for a build that publishes the
        /// counter without the ready flag.
        /// </summary>
        private const int BridgeConfirmationsToFinish = 2;

        /// <summary>
        /// Used when TPPA never reported its heartbeat timings: if nothing at all arrives from TPPA for
        /// this long while a session is open, the session is abandoned instead of running forever.
        /// </summary>
        private const int BridgeSilenceFallbackMs = 30000;

        private readonly BridgeClient _client;
        private readonly HardwareAligner _aligner;
        private readonly PluginSettings _settings;

        private readonly ConcurrentQueue<BridgeEnvelope> _queue = new ConcurrentQueue<BridgeEnvelope>();
        private readonly SemaphoreSlim _queueSignal = new SemaphoreSlim(0);
        private readonly object _gate = new object();
        private readonly System.Timers.Timer _keepAliveTimer;
        private readonly System.Timers.Timer _watchdogTimer;

        private CancellationTokenSource _sessionCts;
        private Task _worker;
        private bool _running;
        private bool _disposed;

        private string _sessionId;
        private string _windowId;
        private TaskCompletionSource<BridgeAdjustmentGrant> _windowGrant;
        private BridgeMeasurement _lastMeasurement;
        private long _lastInboundTicks;
        private int _unusableMeasurements;
        private int _moveCounter;
        private string _statusText = "Idle";
        private bool _paused;
        private bool _resumeNeedsMeasurement;
        private bool _stopping;

        /// <summary>
        /// Minimum time between two connection ladders. TPPA heartbeats the "reference sweep finished" state
        /// every two seconds while it waits for the hardware, and one ladder opens every COM port, so the retries
        /// are spaced out instead of running on every heartbeat.
        /// </summary>
        private const int HardwareConnectRetryIntervalMs = 10000;

        /// <summary>Ticks of the last connection ladder, so two of them are never closer than the interval.</summary>
        private long _lastHardwareConnectTicks;

        /// <summary>1 while a connection ladder runs, so two of them never open the same port at once.</summary>
        private int _hardwareConnectBusy;

        /// <summary>1 once TPPA finished the three reference points and hands the correction over.</summary>
        private int _handoverStarted;

        /// <summary>
        /// 1 once the controller asked TPPA to hold the run before the first correction move, so the pause is asked for
        /// once per session - and only when the run really has to move the axes.
        /// </summary>
        private int _pausedBeforeCorrection;

        /// <summary>True while TPPA reported that the operator paused the run.</summary>
        private bool IsPaused
        {
            get { lock (_gate) { return _paused; } }
        }

        /// <summary>
        /// True from the moment a stop is served until the next session starts. The worker wakes up as soon
        /// as the pending move is released and must not report the move it just aborted as a hardware fault.
        /// </summary>
        private bool IsStopping
        {
            get { lock (_gate) { return _stopping; } }
        }

        public BridgeRunner(BridgeClient client, HardwareAligner aligner, PluginSettings settings)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _aligner = aligner ?? throw new ArgumentNullException(nameof(aligner));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));

            _keepAliveTimer = new System.Timers.Timer(2000) { AutoReset = true };
            _keepAliveTimer.Elapsed += OnKeepAliveTick;

            _watchdogTimer = new System.Timers.Timer(1000) { AutoReset = true };
            _watchdogTimer.Elapsed += OnWatchdogTick;

            // TPPA asks for this controller's readiness before it hands the correction over, so a busy
            // controller has to announce that as soon as the axes go busy or idle again.
            _aligner.ReadinessChanged += OnAlignerReadinessChanged;

            // A firmware error or warning during a session cancels it immediately instead of letting TPPA
            // measure on hardware that reported a problem.
            _aligner.DriverErrorStateChanged += OnDriverErrorStateChanged;
        }

        /// <summary>Raised whenever <see cref="StatusText"/> changes.</summary>
        public event EventHandler StatusChanged;

        /// <summary>
        /// Raised with true while a TPPA session owns the axes. The options page uses it to lock the
        /// manual and software-alignment controls, so two writers never drive the motors at once.
        /// </summary>
        public event EventHandler<bool> SessionActiveChanged;

        /// <summary>Short description of what the controller is doing.</summary>
        public string StatusText
        {
            get { lock (_gate) { return _statusText; } }
        }

        public bool IsSessionRunning
        {
            get { lock (_gate) { return _sessionCts != null && !_sessionCts.IsCancellationRequested; } }
        }

        /// <summary>
        /// True once TPPA has finished the three reference points and the correction is being handed over.
        /// Before that moment the axes are still TPPA's own business and a manual STOP on the dock must not
        /// cancel a polar alignment run that is only being prepared.
        /// </summary>
        public bool IsCancelAllowed
        {
            get { return IsSessionRunning && Volatile.Read(ref _handoverStarted) == 1; }
        }

        public string SessionId
        {
            get { lock (_gate) { return _sessionId; } }
        }

        public void Start()
        {
            if (_running || _disposed) { return; }
            _running = true;
            _worker = Task.Run(() => WorkerLoopAsync());
            _client.EnvelopeReceived += OnEnvelopeReceived;
            Logger.Info("[MLAstro][Broker] External correction runner started.");
        }

        public void Stop()
        {
            if (!_running) { return; }
            _running = false;
            _client.EnvelopeReceived -= OnEnvelopeReceived;
            CancelSessionInternal();
            _queueSignal.Release();
            SetStatus("Idle");
            Logger.Info("[MLAstro][Broker] External correction runner stopped.");
        }

        /// <summary>
        /// Tells TPPA that the broker was switched off while no session is running. Without it TPPA can only notice
        /// by the controller falling silent, so it keeps the controller in its alignment system list until its
        /// presence window expires. Published on the normal command topic without a session id, because there is no
        /// session to address.
        /// </summary>
        public async Task NotifyBrokerDisabledAsync()
        {
            await _client.PublishAsync(BridgeKind.Cancel,
                                       null,
                                       new ControllerCancelPayload { Reason = BridgeReason.BrokerDisabled })
                         .ConfigureAwait(false);
        }

        /// <summary>Aborts a running session on purpose (user pressed Stop, or the firmware reported an error).</summary>
        public async Task AbortSessionAsync(string reason, string note = null)
        {
            BridgeEnvelope sessionEnvelope;
            lock (_gate)
            {
                sessionEnvelope = _sessionId == null
                    ? null
                    : BridgeEnvelope.Create(BridgeKind.Cancel,
                                                        _sessionId,
                                                        Guid.NewGuid().ToString("N"),
                                                        null,
                                                        0,
                                                        BridgeContract.BridgeRecipient,
                                                        new ControllerCancelPayload { Reason = reason, Note = note });
            }

            // STOP first, cancel second: the axes must be told to stop before the session token is
            // cancelled, so the STOP never loses the race with the abort.
            lock (_gate) { _stopping = true; }
            await _aligner.StopAsync().ConfigureAwait(false);
            CancelSessionInternal();

            if (sessionEnvelope != null)
            {
                await _client.PublishAsync(BridgeKind.Cancel,
                                           sessionEnvelope.SessionId,
                                           sessionEnvelope.Payload.ToObject<ControllerCancelPayload>())
                             .ConfigureAwait(false);
            }

            SetStatus("Idle");
        }

        /// <summary>Codes of the incident being collected before the cancel is published.</summary>
        private readonly Dictionary<string, int> _pendingDriverErrorCodes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>1 while a cancel waits for the remaining codes of the same incident.</summary>
        private int _driverErrorCancelPending;

        /// <summary>True while the firmware-error cancel is armed (its codes are still being collected).</summary>
        private bool IsDriverErrorCancelArmed => Volatile.Read(ref _driverErrorCancelPending) == 1;

        /// <summary>
        /// The firmware clears the "command refused" bits of its ERROR telemetry about 1.5 s after the refusal and
        /// one align leg can be refused before the other, so the cancel waits this long to collect every code of
        /// the same incident (both axes out of soft limit end up in ONE note instead of only the first one).
        /// </summary>
        private const int DriverErrorCollectMilliseconds = 1000;

        /// <summary>
        /// The firmware reported a driver error or warning while a session is running: TPPA is told to cancel,
        /// because the correction cannot be trusted once the hardware is in an error state. The cancel is armed
        /// here and published a moment later, so all codes of the incident travel in one note.
        /// </summary>
        private void OnDriverErrorStateChanged(object sender, DriverErrorState state)
        {
            if (state == null || state.IsClean || !IsSessionRunning) { return; }

            bool arm;
            lock (_pendingDriverErrorCodes)
            {
                foreach (var kv in state.Codes.Where(kv => kv.Value != 0))
                {
                    _pendingDriverErrorCodes[kv.Key] = kv.Value;
                }

                arm = Interlocked.Exchange(ref _driverErrorCancelPending, 1) == 0;
            }

            if (!arm)
            {
                // The cancel of this incident is already armed: it reads this code from the collected set.
                return;
            }

            SetStatus("Firmware error - cancelling...");
            _ = Task.Run(CancelAfterDriverErrorAsync);
        }

        /// <summary>
        /// Waits <see cref="DriverErrorCollectMilliseconds"/> so every code of the incident is in, then publishes
        /// the cancel with the collected codes.
        /// </summary>
        private async Task CancelAfterDriverErrorAsync()
        {
            try
            {
                await Task.Delay(DriverErrorCollectMilliseconds).ConfigureAwait(false);

                DriverErrorState collected;
                lock (_pendingDriverErrorCodes)
                {
                    collected = new DriverErrorState(new Dictionary<string, int>(_pendingDriverErrorCodes, StringComparer.OrdinalIgnoreCase));
                    _pendingDriverErrorCodes.Clear();
                    Interlocked.Exchange(ref _driverErrorCancelPending, 0);
                }

                var summary = DescribeActiveCodes(collected);
                var note = DescribeDriverNote(collected, summary);
                Logger.Error($"[MLAstro][Broker] The firmware reported an error during the session: {summary}" +
                             $" [{DescribeCodeTokens(collected)}]");
                // The status line is a single row, so the bullet list of the toast is flattened there.
                SetStatus($"Firmware error - cancelling: {summary.Replace(Environment.NewLine, " | ")}");
                await AbortSessionAsync(BridgeReason.ControllerFault, note).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Error($"[MLAstro][Broker] Failed to cancel the session after a firmware error: {ex.Message}");
            }
        }

        /// <summary>
        /// Human-readable form of the active codes, one bullet per line and without the raw telemetry tokens:
        /// TPPA prints this text in its toast, so it has to read as a list ("- ALT align target out of soft limit").
        /// </summary>
        private static string DescribeActiveCodes(DriverErrorState state)
        {
            var active = state.Codes
                              .Where(kv => kv.Value != 0)
                              .Select(kv => "- " + DriverErrorState.Describe(kv.Key))
                              .Distinct()
                              .ToArray();

            return active.Length == 0 ? "the firmware reported a driver error" : string.Join(Environment.NewLine, active);
        }

        /// <summary>The raw telemetry tokens, for the log only (the toast stays human-readable).</summary>
        private static string DescribeCodeTokens(DriverErrorState state) =>
            string.Join(",",
                        state.Codes.Where(kv => kv.Value != 0)
                                   .Select(kv => $"{kv.Key}={kv.Value}"));

        /// <summary>
        /// Note that travels with the cancel. The manual recovery steps belong to the soft-limit procedure, so
        /// they are only attached when the stop was caused by soft limits AND nothing else: with any other code
        /// in the list there is a real hardware problem, and walking the operator to the tripod while an axis is
        /// not even answering would be wrong advice - the note then stays a plain list of what the firmware saw.
        /// </summary>
        private static string DescribeDriverNote(DriverErrorState state, string summary)
        {
            var active = state.Codes.Where(kv => kv.Value != 0).Select(kv => kv.Key).ToArray();
            var softLimitOnly = active.Length > 0 && active.All(IsSoftLimitCode);
            return softLimitOnly ? summary + Environment.NewLine + SoftLimitRecovery : summary;
        }

        private static bool IsSoftLimitCode(string code) => code switch
        {
            "AzSL" => true,     // AZ soft limit reached
            "AlSL" => true,     // ALT soft limit reached
            "RfRelAz" => true,  // AZ relative move refused
            "RfRelAl" => true,  // ALT relative move refused
            "RfAlnAz" => true,  // AZ align target out of limit
            "RfAlnAl" => true,  // ALT align target out of limit
            "RfAlnOv" => true,  // ALT align overshoot leg out of limit
            "RfJogAz" => true,  // AZ jog refused
            "RfJogAl" => true,  // ALT jog refused
            _ => false
        };

        /// <summary>Manual recovery for a soft-limit stop, as defined by the hardware procedure.</summary>
        private static string SoftLimitRecovery =>
            string.Join(Environment.NewLine,
                        "Soft limit - recover by hand:",
                        "1) Press RETURN TO HOME on MLAstro plugin and wait for the mount to re-centre.",
                        "2) Turn the tripod base by hand towards a smaller error while PA keeps measuring (without Assign MLAstro plugin).",
                        "3) When the error is inside the soft-limit range, assign MLAstro again so it can refine automatically.");

        // ===== inbound =====

        private void OnEnvelopeReceived(object sender, BridgeEnvelope envelope)
        {
            if (envelope == null || !_running) { return; }

            // Every message from TPPA - a measurement, a heartbeat or a state update - keeps the silence
            // watchdog quiet for another interval.
            Volatile.Write(ref _lastInboundTicks, DateTime.UtcNow.Ticks);

            // TPPA addresses the session it runs and drops every answer that carries a different id. A session state
            // that was missed must not leave the controller answering under the id of its previous session, so the id
            // is adopted from any message that carries one.
            if (!string.IsNullOrEmpty(envelope.SessionId))
            {
                lock (_gate) { _sessionId = envelope.SessionId; }
            }

            switch (envelope.Kind)
            {
                case BridgeKind.AdjustmentGranted:
                    HandleAdjustmentGranted(envelope);
                    return;

                case BridgeKind.StopRequested:
                    // Answer immediately: the alignment loop may be minutes deep inside a move.
                    _ = HandleStopRequestAsync(envelope);
                    return;

                case BridgeKind.PauseRequested:
                    // Same reason: a pause has to stop the axes while a move is running, so it must not
                    // wait behind the worker queue.
                    _ = HandlePauseRequestAsync(envelope);
                    return;

                case BridgeKind.SessionEnded:
                    CancelSessionInternal();
                    Enqueue(envelope);
                    return;

                default:
                    Enqueue(envelope);
                    return;
            }
        }

        private void Enqueue(BridgeEnvelope envelope)
        {
            _queue.Enqueue(envelope);
            try { _queueSignal.Release(); } catch (SemaphoreFullException) { }
        }

        private void HandleAdjustmentGranted(BridgeEnvelope envelope)
        {
            TaskCompletionSource<BridgeAdjustmentGrant> pending;
            lock (_gate)
            {
                pending = _windowGrant;
            }

            var grant = envelope.PayloadAs<BridgeAdjustmentGrant>();
            if (grant == null) { return; }

            lock (_gate)
            {
                _windowId = grant.WindowId;
            }

            pending?.TrySetResult(grant);
            StartKeepAlive(grant);
        }

        private async Task HandleStopRequestAsync(BridgeEnvelope envelope)
        {
            var request = envelope.PayloadAs<BridgeStopRequest>();
            var reason = request?.Reason ?? BridgeReason.UserStop;
            Logger.Info($"[MLAstro][Broker] TPPA requested a stop: {reason}.");

            // Set before the move is released: AbortPendingMove wakes the worker immediately, and a worker
            // that sees the aborted move as a fault would stop the axes a second time.
            lock (_gate) { _stopping = true; }

            // STOP first, cancel second: the axes must be told to stop even when the session is already
            // going away, and a STOP that is sent after the cancellation can lose the race with it.
            var stopStatus = BridgeHardwareStopStatus.Ok;
            try
            {
                _aligner.AbortPendingMove();
                if (await _aligner.StopAsync().ConfigureAwait(false))
                {
                    Logger.Info("[MLAstro][Broker] Stop request: STOP:1 sent to the firmware before cancelling.");
                }
                else
                {
                    Logger.Warning("[MLAstro][Broker] Stop request: no STOP could be sent to the firmware.");
                    stopStatus = BridgeHardwareStopStatus.Unknown;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[MLAstro][Broker] Stop failed: {ex.Message}");
                stopStatus = BridgeHardwareStopStatus.Fault;
            }

            CancelSessionInternal();

            await _client.PublishAsync(BridgeKind.Stopped,
                                       envelope.SessionId,
                                       new ControllerStoppedPayload { Reason = reason, HardwareStopStatus = stopStatus })
                         .ConfigureAwait(false);

            SetStatus("Stopped by TPPA");
        }

        /// <summary>
        /// The operator paused or resumed the run in TPPA. A pause stops the axes - only when they are
        /// really moving - and blocks new moves; a resume asks for the measurement the pause interrupted,
        /// because TPPA is waiting for a request that would otherwise never arrive.
        /// </summary>
        private async Task HandlePauseRequestAsync(BridgeEnvelope envelope)
        {
            var request = envelope.PayloadAs<BridgePauseRequest>();
            var paused = request?.Paused == true;
            var reason = request?.Reason ?? (paused ? BridgeReason.Paused : BridgeReason.Resumed);

            lock (_gate)
            {
                _paused = paused;
            }

            if (paused)
            {
                Logger.Info($"[MLAstro][Broker] TPPA paused the run ({reason}).");
                SetStatus("Paused by TPPA");

                try
                {
                    // A STOP is only useful while the axes really turn; when they do not, the capture
                    // window that is still waiting for its grant is dropped instead, so the move that was
                    // already planned never starts after the pause.
                    var stopped = await _aligner.StopMoveAsync().ConfigureAwait(false);
                    var windowDropped = CancelPendingWindowRequest();
                    var axesIdle = true;

                    if (stopped)
                    {
                        // The STOP is not the end of the travel: the firmware reports READY at once while the axes still
                        // coast. The pause waits until that idle really held, and the interrupted step forgets what it
                        // was doing, so the next move is planned from the new measurement alone.
                        ResetDirectionTrackingAfterInterruption();
                        axesIdle = await _aligner.WaitUntilIdleAsync(PauseSettleTimeout).ConfigureAwait(false);
                    }

                    bool answerPending;
                    lock (_gate)
                    {
                        // A measurement that was handed over and is not answered yet: TPPA waits for that answer and
                        // holds it until the resume, so the pause answers it here instead.
                        answerPending = _resumeNeedsMeasurement || stopped || windowDropped;
                        if (answerPending) { _resumeNeedsMeasurement = true; }
                    }

                    Logger.Info($"[MLAstro][Broker] Pause handled: stopped={stopped}, axes idle={axesIdle}, dropped pending window={windowDropped}.");

                    if (answerPending)
                    {
                        await PublishPausedAnswerAsync(reason).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"[MLAstro][Broker] Failed to stop the move for the pause: {ex.Message}");
                }
                return;
            }

            Logger.Info("[MLAstro][Broker] TPPA resumed the run.");

            lock (_gate)
            {
                // Nothing is requested here any more: TPPA publishes its next measurement as soon as it is out of the
                // pause, so a request of our own only drew "no measurement is pending" on every single resume. The
                // answer a pause owes is sent while the pause happens, which is when TPPA is still holding that wait.
                _resumeNeedsMeasurement = false;
            }

            SetStatus("Waiting for the next measurement");
        }

        /// <summary>
        /// Answers the measurement the pause interrupted, so TPPA is not left waiting for a move that is never going to
        /// happen. TPPA knows what a pause is, so this only has to say "measure again"; the resume asks for the next
        /// measurement the same way.
        /// </summary>
        private async Task PublishPausedAnswerAsync(string reason)
        {
            string sessionId;
            lock (_gate) { sessionId = _sessionId; }

            if (sessionId == null || !IsSessionRunning) { return; }

            try
            {
                await _client.PublishAsync(BridgeKind.RequestMeasurement,
                                           sessionId,
                                           new BridgeMeasurementRequest
                                           {
                                               WindowId = null,
                                               StationaryAndSettled = true,
                                               // The settle of the SOFTWARE SETTING belongs to the capture that follows the
                                               // resume: the axes were stopped for the pause, so their mechanics need that
                                               // moment then, not while the run is still standing still.
                                               SettleSeconds = _settings.AutomatedAdjustmentSettleTime > 0
                                                   ? _settings.AutomatedAdjustmentSettleTime
                                                   : (double?)null,
                                               Reason = string.IsNullOrWhiteSpace(reason) ? BridgeReason.Paused : reason
                                           }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Warning($"[MLAstro][Broker] Could not answer the measurement that the pause interrupted: {ex.Message}");
            }
        }

        // ===== worker =====

        private async Task WorkerLoopAsync()
        {
            while (_running)
            {
                await _queueSignal.WaitAsync().ConfigureAwait(false);
                while (_running && _queue.TryDequeue(out var envelope))
                {
                    try
                    {
                        await HandleEnvelopeAsync(envelope).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"[MLAstro][Broker] Handling '{envelope.Kind}' failed: {ex}");
                    }
                }
            }
        }

        private async Task HandleEnvelopeAsync(BridgeEnvelope envelope)
        {
            switch (envelope.Kind)
            {
                case BridgeKind.SessionState:
                    await HandleSessionStateAsync(envelope).ConfigureAwait(false);
                    return;

                case BridgeKind.Measurement:
                    await HandleMeasurementAsync(envelope).ConfigureAwait(false);
                    return;

                case BridgeKind.SessionEnded:
                    HandleSessionEnded(envelope);
                    return;

                case BridgeKind.Fault:
                    Logger.Warning("[MLAstro][Broker] TPPA reported a fault.");
                    return;

                default:
                    Logger.Debug($"[MLAstro][Broker] Ignoring '{envelope.Kind}'.");
                    return;
            }
        }

        private async Task HandleSessionStateAsync(BridgeEnvelope envelope)
        {
            var state = envelope.PayloadAs<BridgeSessionState>();
            if (state == null) { return; }

            Logger.Debug($"[MLAstro][Broker] TPPA session state: {state.State} ({state.Reason}).");

            if (string.Equals(state.State, BridgeState.Preparing, StringComparison.Ordinal))
            {
                await HandleSessionPreparingAsync(envelope.SessionId).ConfigureAwait(false);
                return;
            }

            if (string.Equals(state.State, BridgeState.Ended, StringComparison.Ordinal))
            {
                HandleSessionEnded(envelope);
                return;
            }

            if (string.Equals(state.State, BridgeState.WaitingForRequest, StringComparison.Ordinal)
                && string.Equals(state.Reason, BridgeReason.MeasurementsFinished, StringComparison.Ordinal))
            {
                // The three reference points are measured: only now are the axes needed. A controller that is
                // still offline brings its hardware up here and reports the result - that report is what TPPA
                // checks before the correction. A failed attempt is not final: TPPA heartbeats this state while
                // it waits, so the ladder is picked up again on the next heartbeat and the hardware can still be
                // plugged in or switched to the other link without the operator touching anything.
                // This state and an incoming measurement are the ONLY two moments the plugin looks for the
                // hardware: nothing is scanned or opened while TPPA is still measuring the reference points.
                Logger.Info($"[MLAstro][Broker] TPPA finished the reference sweep. Hardware connected: {_aligner.IsConnected}.");
                SetStatus("Connecting the hardware...");

                if (!await EnsureHardwareConnectedAsync().ConfigureAwait(false))
                {
                    SetStatus("Hardware not connected - still looking for the controller");
                    return;
                }

                if (!IsSessionRunning)
                {
                    // The session never started (the link was down when TPPA opened it): start it now so
                    // the measurements that follow are accepted.
                    ResetSessionState();
                    OnSessionStarted();
                }

                SetStatus("TPPA session: controller ready");
                // The sweep is over and the hardware is up: from here on a manual STOP on the dock may cancel
                // the session (it is set after ResetSessionState, which clears it for a fresh session).
                Volatile.Write(ref _handoverStarted, 1);
                await PublishReadinessAsync(SessionId, SessionId).ConfigureAwait(false);
                return;
            }

            SetStatus($"TPPA session: {state.State}");

            if (string.Equals(state.Reason, BridgeReason.SilenceTimeout, StringComparison.Ordinal))
            {
                Logger.Warning("[MLAstro][Broker] TPPA closed the capture window because the controller went silent.");
                StopKeepAlive();
            }
        }

        private async Task HandleSessionPreparingAsync(string sessionId)
        {
            if (!string.IsNullOrEmpty(sessionId))
            {
                lock (_gate)
                {
                    _sessionId = sessionId;
                }
            }

            BridgeSettings.FromPluginSettings(_settings);

            ResetSessionState();
            SetStatus(_aligner.IsConnected
                ? "TPPA session: controller ready"
                : "TPPA session: hardware offline - connecting after the reference sweep");

            // Nothing is connected here on purpose: TPPA is still measuring the three reference points and
            // does not need the axes yet. The link is brought up once the sweep is finished (see
            // HandleSessionStateAsync), so the hand-over prompt sees the real readiness.
            await PublishReadinessAsync(sessionId, SessionId).ConfigureAwait(false);

            OnSessionStarted();
        }

        /// <summary>
        /// Makes sure the alignment hardware is reachable, the way the operator asked: the connection selected in
        /// the settings is tried first, then the other way of looking for the controller, then the last option.
        /// The ladder is rate limited and never runs twice at the same time, so TPPA heartbeats cannot turn into a
        /// permanent COM-port scan. Returns true when the controller can be driven.
        /// </summary>
        private async Task<bool> EnsureHardwareConnectedAsync(bool force = false)
        {
            if (_aligner.IsConnected) { return true; }
            if (Interlocked.CompareExchange(ref _hardwareConnectBusy, 1, 0) != 0) { return false; }

            try
            {
                var now = DateTime.UtcNow.Ticks;
                var last = Volatile.Read(ref _lastHardwareConnectTicks);
                if (!force && last != 0 && TimeSpan.FromTicks(now - last).TotalMilliseconds < HardwareConnectRetryIntervalMs)
                {
                    return false;
                }

                Volatile.Write(ref _lastHardwareConnectTicks, now);
                return await TryConnectHardwareAsync().ConfigureAwait(false);
            }
            finally
            {
                Volatile.Write(ref _hardwareConnectBusy, 0);
            }
        }

        /// <summary>One rung of the connection ladder.</summary>
        private readonly struct ConnectAttempt
        {
            public ConnectAttempt(MlastroTransportMode transport, bool selectedPortOnly)
            {
                Transport = transport;
                SelectedPortOnly = selectedPortOnly;
            }

            public MlastroTransportMode Transport { get; }

            /// <summary>Serial only: try the port the operator selected and nothing else.</summary>
            public bool SelectedPortOnly { get; }
        }

        /// <summary>
        /// The ladder the operator asked for: the selected connection first, then the other one, then the last
        /// option, so a controller used over the cable today and over the wireless link tomorrow is found without
        /// changing the settings.
        /// Wireless selected: wireless - the selected COM port - every COM port.
        /// Serial selected: the selected COM port - every COM port - wireless.
        /// </summary>
        private IReadOnlyList<ConnectAttempt> BuildConnectAttempts()
        {
            if (_settings.TransportMode == MlastroTransportMode.Wireless)
            {
                return new[]
                {
                    new ConnectAttempt(MlastroTransportMode.Wireless, false),
                    new ConnectAttempt(MlastroTransportMode.Serial, true),
                    new ConnectAttempt(MlastroTransportMode.Serial, false)
                };
            }

            return new[]
            {
                new ConnectAttempt(MlastroTransportMode.Serial, true),
                new ConnectAttempt(MlastroTransportMode.Serial, false),
                new ConnectAttempt(MlastroTransportMode.Wireless, false)
            };
        }

        /// <summary>Runs the whole ladder, one rung after the other, until a link answers.</summary>
        private async Task<bool> TryConnectHardwareAsync()
        {
            if (_aligner.IsConnected) { return true; }

            var attempts = BuildConnectAttempts();
            for (var i = 0; i < attempts.Count; i++)
            {
                var attempt = attempts[i];
                var name = attempt.Transport == MlastroTransportMode.Wireless
                    ? "the wireless link"
                    : attempt.SelectedPortOnly
                        ? $"the selected COM port ({_settings.ComPort ?? "none"})"
                        : "every COM port";

                SetStatus($"Connecting {i + 1}/{attempts.Count}: {name}...");
                Logger.Info($"[MLAstro][Broker] Connection attempt {i + 1}/{attempts.Count}: {name}.");

                if (await TryConnectTransportAsync(attempt.Transport, attempt.SelectedPortOnly).ConfigureAwait(false))
                {
                    return true;
                }
            }

            Logger.Warning("[MLAstro][Broker] No link to the controller after the whole connection ladder.");
            SetStatus("Hardware not connected");
            return false;
        }

        private async Task<bool> TryConnectTransportAsync(MlastroTransportMode transport, bool selectedPortOnly)
        {
            // Ports are opened and closed on purpose while looking for the controller: a running TPPA session
            // must not read those link changes as "the firmware link was lost".
            _aligner.SuppressLinkLossNotification = true;
            try
            {
                if (transport == MlastroTransportMode.Wireless)
                {
                    var wireless = MlastroWebSocketService.Instance;
                    if (wireless == null)
                    {
                        Logger.Warning("[MLAstro][Broker] The wireless service is not available.");
                        return false;
                    }

                    // A WebSocket that is up is only usable when the device answered its handshake, so the
                    // same rule as serial applies here.
                    var connected = await wireless.ConnectAsync().ConfigureAwait(false)
                                    && await _aligner.SendHandshakeAsync().ConfigureAwait(false);
                    if (connected)
                    {
                        // Keep the profile in step with the link that really came up.
                        _settings.TransportMode = MlastroTransportMode.Wireless;
                        return true;
                    }

                    if (_aligner.IsConnected) { _aligner.Disconnect(); }
                    return false;
                }

                // Serial: every candidate port is opened and greeted exactly once - the connect itself sends
                // the handshake, so the port that answers is the controller. No separate scan probe, which
                // would greet the device a second time. The selected port is one rung of the ladder and the
                // other ports the next one.
                var candidates = new List<string>();
                if (!string.IsNullOrWhiteSpace(_settings.ComPort))
                {
                    candidates.Add(_settings.ComPort);
                }

                if (!selectedPortOnly)
                {
                    foreach (var enumerated in _aligner.AvailablePorts)
                    {
                        if (!candidates.Contains(enumerated, StringComparer.OrdinalIgnoreCase))
                        {
                            candidates.Add(enumerated);
                        }
                    }
                }

                if (candidates.Count == 0)
                {
                    Logger.Warning("[MLAstro][Broker] No COM port to try: none is selected and the system lists none.");
                    return false;
                }

                foreach (var candidate in candidates)
                {
                    SetStatus($"Scanning the COM ports: trying {candidate}...");
                    if (!await _aligner.ConnectSerialAsync(candidate, _settings.BaudRate).ConfigureAwait(false))
                    {
                        continue;
                    }

                    if (await _aligner.WaitForHandshakeAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false))
                    {
                        _settings.TransportMode = MlastroTransportMode.Serial;
                        return true;
                    }

                    // The port opened but the controller did not answer: not our device, try the next one.
                    Logger.Warning($"[MLAstro][Broker] {candidate} opened but the controller did not answer.");
                    _aligner.Disconnect();
                }

                Logger.Warning("[MLAstro][Broker] No controller answered on any COM port.");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Warning($"[MLAstro][Broker] Connecting over {transport} failed: {ex.Message}");
                return false;
            }
            finally
            {
                _aligner.SuppressLinkLossNotification = false;
            }
        }

        /// <summary>
        /// Tells TPPA whether the axes can be handed over right now. The controller is the only side that
        /// sees the hardware, so the answer travels on the existing ControllerReady message: TPPA caches it
        /// and reads it again at the hand-over prompt and when the operator presses RESUME.
        /// </summary>
        private async Task PublishReadinessAsync(string sessionId, string activeSessionId)
        {
            // Readiness always reports the state of the axes: a travel that is still running is handed over as not ready,
            // and one that ended while the operator was deciding in front of the confirmation dialog becomes ready at
            // once. The take-over flag only refines the note, it must never hold the ready state back - otherwise the
            // operator's thinking time would eat TPPA's readiness budget and the run would fail on a controller that is
            // standing still.
            var ready = _aligner.IsHardwareReady;
            var status = _aligner.DeviceStatus;
            var note = ready
                ? null
                : _sessionPreparing && _aligner.ReportsAxesIdle
                    ? "the controller is still taking the axes over"
                    : _aligner.IsConnected
                        ? $"hardware busy (STATUS: {status})"
                        : "no hardware link";

            await _client.PublishAsync(BridgeKind.ControllerReady,
                                       string.IsNullOrWhiteSpace(sessionId) ? activeSessionId : sessionId,
                                       new ControllerReadyPayload
                                       {
                                           Controller = BridgeContract.ControllerName,
                                           ControllerVersion = typeof(BridgeRunner).Assembly.GetName().Version?.ToString(),
                                           HardwareConnected = _aligner.IsConnected,
                                           HardwareReady = ready,
                                           LinkPath = _aligner.LinkDescription,
                                           Note = note
                                       }).ConfigureAwait(false);

            Logger.Info($"[MLAstro][Broker] Readiness reported to TPPA: hardwareReady={ready}, status={status}");
        }

        private void OnAlignerReadinessChanged(object sender, EventArgs e)
        {
            if (!IsSessionRunning)
            {
                return;
            }

            var sessionId = SessionId;
            _ = Task.Run(async () =>
            {
                try
                {
                    await PublishReadinessAsync(sessionId, sessionId).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Logger.Warning($"[MLAstro][Broker] Failed to report readiness to TPPA: {ex.Message}");
                }
            });
        }

        private void OnSessionStarted()
        {
            CancellationTokenSource cts;
            lock (_gate)
            {
                _sessionCts?.Dispose();
                _sessionCts = new CancellationTokenSource();
                cts = _sessionCts;
            }

            // The length of a session is TPPA's business: it owns its own safety time limit and asks for a
            // stop when that limit expires. This controller only watches that TPPA is still there at all.
            _lastInboundTicks = DateTime.UtcNow.Ticks;
            _watchdogTimer.Start();

            Logger.Info($"[MLAstro][Broker] Session {SessionId} started. Watching for TPPA silence.");
            NotifySessionActive(true);

            // The hardware is brought up as soon as the session exists instead of when TPPA asks for the
            // correction: TPPA checks that the axes stand still before it captures the three reference frames,
            // and a controller that only connects afterwards can never answer that check.
            _ = Task.Run(() => PrepareHardwareForSessionAsync(cts.Token));
        }

        /// <summary>
        /// Asks the operator whether a travel that is still running may be stopped for the alignment run. The dialog
        /// lives on the UI side; a controller without one simply takes the axes.
        /// </summary>
        public event EventHandler<HardwareBusyConfirmationRequest> HardwareBusyConfirmationRequested;

        /// <summary>
        /// True while the session is taking the hardware over: readiness is reported as "not ready" until this is done,
        /// so TPPA cannot take the controller as ready while its axes are still being brought up.
        /// </summary>
        private volatile bool _sessionPreparing;

        /// <summary>
        /// Opens the link and gets the axes standing still before TPPA captures the reference frames, so the run can
        /// report the readiness TPPA waits for. A travel that is still running is only stopped after the operator
        /// confirmed it, because that travel may be one they started themselves.
        /// </summary>
        private async Task PrepareHardwareForSessionAsync(CancellationToken token)
        {
            _sessionPreparing = true;
            var reportReady = false;

            try
            {
                if (!await EnsureHardwareConnectedAsync(force: true).ConfigureAwait(false))
                {
                    Logger.Warning("[MLAstro][Broker] No link to the alignment hardware when the session started; reporting not ready.");
                    SetStatus("No link to the alignment hardware");
                    // Comes out as "not ready" because the session is still preparing, which is exactly what TPPA has to
                    // read while the hardware is not taken over yet.
                    await PublishReadinessAsync(SessionId, SessionId).ConfigureAwait(false);
                    return;
                }

                if (!_aligner.IsHardwareReady)
                {
                    if (_aligner.ReportsAxesIdle)
                    {
                        // The device was just connected: the firmware already reports the axes as idle and only the ready
                        // debounce has to elapse. That is not a travel anybody has to confirm.
                        Logger.Info("[MLAstro][Broker] The hardware reports its axes as idle; waiting for the ready debounce.");
                        var settled = await _aligner.WaitUntilIdleAsync(PauseSettleTimeout).ConfigureAwait(false);
                        Logger.Info(settled
                            ? "[MLAstro][Broker] The axes are idle and ready; readiness is reported to TPPA."
                            : $"[MLAstro][Broker] The axes did not hold their idle state for the ready debounce within {PauseSettleTimeout.TotalSeconds:0} s.");
                    }
                    else
                    {
                        Logger.Info($"[MLAstro][Broker] The axes are not idle when the session starts (STATUS: {_aligner.DeviceStatus}).");
                        SetStatus($"Axes turning ({_aligner.DeviceStatus}) - waiting for the operator");

                        if (!ConfirmStopRunningTravel())
                        {
                            // Without the confirmation the axes are left exactly as they are: this side keeps reporting that
                            // it is not ready, so TPPA's own readiness timeout ends the run and reports it. The controller
                            // does not take that decision away from the operator by cancelling the session here.
                            Logger.Warning("[MLAstro][Broker] The STOP was not confirmed; the axes are left as they are and TPPA reports the run as not ready.");
                            SetStatus("Axes left as they are - the alignment run cannot start");
                            return;
                        }

                        // Nothing is left to stop when the travel ended by itself while the operator was deciding.
                        if (!_aligner.IsHardwareReady)
                        {
                            await _aligner.StopAsync().ConfigureAwait(false);
                        }

                        var idle = await _aligner.WaitUntilIdleAsync(PauseSettleTimeout).ConfigureAwait(false);
                        Logger.Info(idle
                            ? "[MLAstro][Broker] The axes stand still; readiness is reported to TPPA."
                            : $"[MLAstro][Broker] The axes did not report idle within {PauseSettleTimeout.TotalSeconds:0} s after the confirmed STOP.");
                    }
                }

                reportReady = true;
            }
            catch (Exception ex)
            {
                Logger.Error($"[MLAstro][Broker] Failed to prepare the hardware for the session: {ex.Message}");
            }
            finally
            {
                // The axes are taken over (or the attempt is over), so readiness reports the real state again.
                _sessionPreparing = false;
            }

            if (reportReady)
            {
                await PublishReadinessAsync(SessionId, SessionId).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// The operator confirms (or refuses) the STOP of a travel that is still running. Returns true when nothing
        /// has to be confirmed or the operator confirmed it.
        /// </summary>
        private bool ConfirmStopRunningTravel()
        {
            var handler = HardwareBusyConfirmationRequested;
            if (handler == null) { return true; }

            var request = new HardwareBusyConfirmationRequest(_aligner.DeviceStatus, _aligner.LinkDescription);
            try
            {
                handler(this, request);
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                return false;
            }

            return request.Confirmed;
        }

        private void HandleSessionEnded(BridgeEnvelope envelope)
        {
            var ended = envelope.PayloadAs<BridgeSessionEnded>();
            Logger.Info($"[MLAstro][Broker] Session ended. Reason: {ended?.Reason}; achieved: {ended?.Achieved}; " +
                        $"final total error: {ended?.TotalErrorArcMin:0.##}' (tolerance {ended?.ToleranceUsedArcMin:0.##}').");

            CancelSessionInternal();
            SetStatus(ended?.Achieved == true
                          ? $"Completed: {ended.TotalErrorArcMin:0.##}'"
                          : $"Session ended ({ended?.Reason ?? "unknown"})");
        }

        private async Task HandleMeasurementAsync(BridgeEnvelope envelope)
        {
            var measurement = envelope.PayloadAs<BridgeMeasurement>();
            if (measurement == null) { return; }

            // TPPA is measuring, so its session is alive even when this side never saw the state message (or
            // abandoned the session after a silence): dropping the measurement would leave TPPA waiting for an
            // answer that never comes, so the session is re-armed instead.
            bool rearm;
            lock (_gate) { rearm = _sessionCts == null; }

            if (!string.IsNullOrEmpty(measurement.SessionId))
            {
                lock (_gate) { _sessionId = measurement.SessionId; }
            }

            if (rearm)
            {
                Logger.Warning("[MLAstro][Broker] Received a measurement without a running session. Re-arming the session.");
                ResetSessionState();
                OnSessionStarted();
            }

            CancellationToken token;
            lock (_gate)
            {
                if (_sessionCts == null)
                {
                    Logger.Warning("[MLAstro][Broker] The session could not be started, so the measurement is ignored.");
                    return;
                }
                _lastMeasurement = measurement;
                token = _sessionCts.Token;
            }

            // The measurement is TPPA's question, so this is the moment the axes have to be there: the whole
            // connection ladder is run here, because a controller that reached its hardware only after the sweep
            // (or that missed the sweep-finished state) must still answer instead of leaving the run hanging.
            if (!await EnsureHardwareConnectedAsync(force: true).ConfigureAwait(false))
            {
                Logger.Warning("[MLAstro][Broker] No link to the alignment hardware: TPPA is told this controller cannot correct.");
                await _client.PublishAsync(BridgeKind.Fault,
                                           SessionId,
                                           new ControllerFaultPayload
                                           {
                                               Reason = BridgeReason.FirmwareDisconnected,
                                               Detail = "No link to the MLAstro controller: TPPA asked for a correction the hardware cannot perform.",
                                               HardwareStopStatus = BridgeHardwareStopStatus.Unknown
                                           }).ConfigureAwait(false);
                return;
            }

            Logger.Info($"[MLAstro][Broker] Measurement #{measurement.SampleIndex}: az {measurement.AzimuthErrorArcMin:0.##}' " +
                        $"({measurement.AzimuthDirectionValue}), alt {measurement.AltitudeErrorArcMin:0.##}' ({measurement.AltitudeDirectionValue}), " +
                        $"total {measurement.TotalErrorArcMin:0.##}', tolerance {measurement.ToleranceArcMin:0.##}', " +
                        $"status {measurement.Status}.");

            if (IsPaused)
            {
                // TPPA stopped capturing, so a move now would turn the axes for nobody. The sample is
                // re-requested when the operator resumes.
                Logger.Info("[MLAstro][Broker] Measurement ignored while the run is paused.");
                lock (_gate) { _resumeNeedsMeasurement = true; }
                SetStatus("Paused by TPPA");
                return;
            }

            // The direction is judged on the SIGN of the error (see TrackAxisError): the sign is the side of the
            // target the error sits on, so a move in the wrong direction keeps the sign of the sweep error and
            // grows there, while a move in the right one - the overshoot leg included - crosses the target.
            var toleranceArcMin = Math.Max(0, measurement.ToleranceArcMin);
            var azGrowth = TrackAxisError("AZ", 0, measurement.AzimuthErrorArcMin, toleranceArcMin);
            if (azGrowth != null && await HandleAxisGrowthAsync(azGrowth, 0).ConfigureAwait(false))
            {
                return;
            }

            var altGrowth = TrackAxisError("ALT", 1, measurement.AltitudeErrorArcMin, toleranceArcMin);
            if (altGrowth != null && await HandleAxisGrowthAsync(altGrowth, 1).ConfigureAwait(false))
            {
                return;
            }

            var settings = BridgeSettings.FromPluginSettings(_settings);
            var warnings = new List<string>();
            var plan = BridgeEngine.CreatePlan(measurement, settings, warnings);
            foreach (var warning in warnings.Where(w => w != null).Distinct())
            {
                Logger.Warning($"[MLAstro][Broker] {warning}");
            }

            if (plan.HoldAzimuth || plan.HoldAltitude)
            {
                // TPPA keeps reporting the full error; the held axis is simply not moved any more, so a
                // passing axis is not nudged away from the tolerance it has already reached.
                var heldAxis = plan.HoldAzimuth ? "AZ" : "ALT";
                Logger.Info($"[MLAstro][Broker] {heldAxis} is already inside the tolerance "
                            + $"({plan.ToleranceArcMin:0.##}') - commanding a zero magnitude for it.");
            }

            if (token.IsCancellationRequested) { return; }

            if (plan.VerifyOnly)
            {
                await HandleVerifyOnlyAsync(measurement, token).ConfigureAwait(false);
                return;
            }

            // The measured error is in and this plan really moves the axes: hold the run here once, so the operator can
            // check the result and the setup before the motors start. A result that is already inside the tolerance
            // never reaches this point (the verify path above answers it), so a run that needs no correction is never
            // paused and TPPA finishes it on its own. TPPA pauses exactly like a pause pressed in its own window, and
            // pressing RESUME there starts the correction.
            if (_settings.AutoPauseAfterReferenceSweep && Interlocked.CompareExchange(ref _pausedBeforeCorrection, 1, 0) == 0)
            {
                await _client.RequestTppaPauseAsync().ConfigureAwait(false);
                const string heldText = "Three point solve finished - the correction is paused. Press RESUME in Three Point Polar Alignment to start it.";
                Logger.Info($"[MLAstro][Broker] Asked TPPA to hold the run before the first move. {heldText}");
                SetStatus("Held before the first move - press RESUME in TPPA");
                try { NINA.Core.Utility.Notification.Notification.ShowInformation(heldText, TimeSpan.FromSeconds(30)); } catch { }
                return;
            }

            // The direction detection watches both axes themselves (see TrackAxisError), so nothing of the
            // measured error has to be remembered here.
            SetStatus($"Adjusting: {plan.Reason}");
            await ExecutePlanAsync(measurement, plan, settings, token).ConfigureAwait(false);
        }

        private async Task HandleVerifyOnlyAsync(BridgeMeasurement measurement, CancellationToken token)
        {
            // No move was planned, so the next measurement must not be judged as if an axis had been commanded.
            _axisCommandedLastStep[0] = false;
            _axisCommandedLastStep[1] = false;

            if (measurement.ToleranceReached || measurement.AutoFinishConditionMet)
            {
                SetStatus($"Within tolerance ({measurement.TotalErrorArcMin:0.##}') - confirming");

                // TPPA owns the finish policy: it counts the consecutive solves that are inside the
                // tolerance and marks the measurement once its own gate is met. The counter in the payload
                // is only a fallback for a TPPA build that publishes it without the ready flag.
                if (measurement.AutoFinishConditionMet || measurement.ConsecutiveBelowTolerance >= BridgeConfirmationsToFinish)
                {
                    Logger.Info("[MLAstro][Broker] Alignment is within tolerance. Asking TPPA to complete the session.");
                    await _client.PublishAsync(BridgeKind.RequestCompletion,
                                               SessionId,
                                               new BridgeCompletionRequest
                                               {
                                                   WindowId = null,
                                                   Reason = BridgeReason.CompletionRequested,
                                                   ConsecutiveBelowTolerance = Math.Max(1, measurement.ConsecutiveBelowTolerance)
                                               }).ConfigureAwait(false);
                    SetStatus("Verifying final alignment");
                    return;
                }
            }
            else
            {
                _unusableMeasurements++;
                Logger.Warning($"[MLAstro][Broker] Measurement is not usable ({measurement.Status}), " +
                               $"attempt {_unusableMeasurements}/{MaxUnusableMeasurements}.");
                if (_unusableMeasurements > MaxUnusableMeasurements)
                {
                    await PublishFaultAndEndAsync(BridgeReason.CaptureFailed,
                                                  $"The polar alignment measurement stayed unusable for {MaxUnusableMeasurements} attempts.")
                                 .ConfigureAwait(false);
                    return;
                }
            }

            if (token.IsCancellationRequested) { return; }

            // Ask for a new measurement without moving: either to confirm a passing sample or to recover
            // from an unusable one.
            await _client.PublishAsync(BridgeKind.RequestMeasurement,
                                       SessionId,
                                       new BridgeMeasurementRequest
                                       {
                                           WindowId = null,
                                           StationaryAndSettled = true,
                                           Reason = BridgeReason.VerifyOnly
                                       }).ConfigureAwait(false);
            SetStatus("Waiting for the next measurement");
        }

        private async Task ExecutePlanAsync(BridgeMeasurement measurement,
                                            CorrectionPlan plan,
                                            BridgeSettings settings,
                                            CancellationToken token)
        {
            _moveCounter++;
            var windowId = await RequestWindowAsync(measurement, plan, token).ConfigureAwait(false);
            if (windowId == null)
            {
                if (IsPaused)
                {
                    // The pause dropped the window: no move was made, and a fresh measurement is asked
                    // for once the run resumes.
                    Logger.Info("[MLAstro][Broker] Move dropped: the run was paused before the window was granted.");
                    lock (_gate) { _resumeNeedsMeasurement = true; }
                    SetStatus("Paused by TPPA");
                    return;
                }

                Logger.Warning("[MLAstro][Broker] TPPA did not grant a capture window in time. Not moving.");
                SetStatus("Capture window not granted");
                return;
            }

            if (IsPaused)
            {
                // The window was granted before the operator paused: dropping the move here is what keeps
                // the axes still until the run resumes. The window is left to expire on TPPA's side
                // because no keep-alive is sent for it any more.
                Logger.Info("[MLAstro][Broker] Move dropped: the run was paused before the move started.");
                StopKeepAlive();
                ClearWindow();
                lock (_gate) { _resumeNeedsMeasurement = true; }
                SetStatus("Paused by TPPA");
                return;
            }

            var aligned = await _aligner.AlignAsync(plan, token).ConfigureAwait(false);
            if (!aligned)
            {
                if (token.IsCancellationRequested || IsStopping)
                {
                    // Stopped on purpose: the session token may not be cancelled yet at this instant.
                    Logger.Info("[MLAstro][Broker] Move aborted because the session was stopped.");
                    return;
                }

                if (IsPaused)
                {
                    // Paused mid-move: the operator stopped the axes on purpose, so this is not a
                    // hardware fault. A fresh measurement is requested when the run resumes.
                    Logger.Info("[MLAstro][Broker] Move aborted because the run was paused.");
                    lock (_gate) { _resumeNeedsMeasurement = true; }
                    SetStatus("Paused by TPPA");
                    return;
                }

                if (IsDriverErrorCancelArmed)
                {
                    // The firmware refused the move AND reported why: the armed error cancel publishes that
                    // reason (with the recovery steps) a moment from now. Raising the generic "move did not
                    // complete" fault here would win the race and hide the real cause from the operator.
                    Logger.Info($"[MLAstro][Broker] The alignment move {_moveCounter} failed while a firmware error was being collected: leaving the cancel to the error handler.");
                    return;
                }

                await PublishFaultAndEndAsync(BridgeReason.ControllerFault,
                                              $"The alignment move {_moveCounter} did not complete on the hardware.")
                             .ConfigureAwait(false);
                return;
            }

            // The next measurement may only judge an axis this step really commanded.
            _axisCommandedLastStep[0] = plan.MoveAzimuth;
            _axisCommandedLastStep[1] = plan.MoveAltitude;

            // No back-off move: the overshoot leg already travelled past the target and the next
            // measurement corrects whatever is left, exactly like the MLAstro TPPA plugin did. Coming back
            // on purpose would put the play back into the axis the overshoot just removed.

            StopKeepAlive();
            ClearWindow();

            // The firmware reports the move as completed once the axes reached the target, but the mechanics
            // still need a moment (backlash release, vibration). The wait itself belongs to TPPA: it is the side
            // that captures, and it already runs the very same wait for the adjustment systems it drives on its
            // own, so the operator sees "Settling" in the TPPA status bar and this status line stays free.
            var settleSeconds = _settings.AutomatedAdjustmentSettleTime;

            if (token.IsCancellationRequested) { return; }

            await _client.PublishAsync(BridgeKind.RequestMeasurement,
                                       SessionId,
                                       new BridgeMeasurementRequest
                                       {
                                           WindowId = windowId,
                                           StationaryAndSettled = true,
                                           SettleSeconds = settleSeconds > 0 ? settleSeconds : (double?)null,
                                           Reason = BridgeReason.StepFinished
                                       }).ConfigureAwait(false);
            SetStatus("Waiting for the next measurement");
        }

        /// <summary>
        /// Releases a capture window request that is still waiting for its grant, so a pause cannot be
        /// followed by a move that was already planned. Returns true when a request was really waiting.
        /// </summary>
        private bool CancelPendingWindowRequest()
        {
            TaskCompletionSource<BridgeAdjustmentGrant> pending;
            lock (_gate)
            {
                pending = _windowGrant;
            }

            return pending != null && pending.TrySetResult(null);
        }

        private async Task<string> RequestWindowAsync(BridgeMeasurement measurement, CorrectionPlan plan, CancellationToken token)
        {
            var grant = new TaskCompletionSource<BridgeAdjustmentGrant>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _windowGrant = grant;
            }

            try
            {
                await _client.PublishAsync(BridgeKind.BeginAdjustment,
                                           SessionId,
                                           new BridgeAdjustmentRequest
                                           {
                                               MeasurementId = measurement.MeasurementId,
                                               PlannedAzimuthArcMin = plan.MoveAzimuth ? plan.AzimuthMagnitudeArcMin : (double?)null,
                                               PlannedAltitudeArcMin = plan.MoveAltitude ? plan.AltitudeMagnitudeArcMin : (double?)null,
                                               DetectingDirection = PlanIsDetectingDirection(plan),
                                               Note = plan.Reason
                                           }).ConfigureAwait(false);

                var timeoutMs = _client.Capabilities?.SilenceTimeoutMs > 0
                    ? Math.Max(WindowGrantTimeoutMs, _client.Capabilities.SilenceTimeoutMs)
                    : WindowGrantTimeoutMs;

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeoutCts.CancelAfter(timeoutMs);
                try
                {
                    var window = await grant.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
                    return window?.WindowId;
                }
                catch (OperationCanceledException)
                {
                    token.ThrowIfCancellationRequested();
                    return null;
                }
            }
            finally
            {
                lock (_gate)
                {
                    _windowGrant = null;
                }
            }
        }

        // ===== keep-alive =====

        private void StartKeepAlive(BridgeAdjustmentGrant grant)
        {
            var heartbeat = _client.Capabilities?.HeartbeatMs ?? 2000;
            // Keep-alive must never depend on the ALIGN call, which can block for a long time.
            _keepAliveTimer.Interval = Math.Max(500, heartbeat);
            _keepAliveTimer.Start();
            Logger.Info($"[MLAstro][Broker] Capture window {grant.WindowId} open; keep-alive every {_keepAliveTimer.Interval:0} ms.");
        }

        private void StopKeepAlive()
        {
            _keepAliveTimer.Stop();
        }

        private void OnKeepAliveTick(object sender, ElapsedEventArgs e)
        {
            string windowId;
            string sessionId;
            lock (_gate)
            {
                windowId = _windowId;
                sessionId = _sessionId;
            }

            if (windowId == null || sessionId == null) { return; }

            _ = _client.PublishAsync(BridgeKind.KeepAlive,
                                     sessionId,
                                     new BridgeKeepAlive { WindowId = windowId, State = "Adjusting" })
                        .ContinueWith(t => Logger.Error(t.Exception), TaskContinuationOptions.OnlyOnFaulted);
        }

        // ===== session bookkeeping =====

        /// <summary>
        /// Ticks once a second while a session is open. TPPA heartbeats the session, so a long silence
        /// means TPPA (or NINA) is gone: the session is abandoned instead of staying open forever with the
        /// manual controls locked out and nobody driving the axes.
        /// </summary>
        private void OnWatchdogTick(object sender, ElapsedEventArgs e)
        {
            if (!IsSessionRunning) { return; }

            // The controller is the only side that sees the hardware: once the axes were handed over, a link that is
            // gone means they can neither be moved nor verified, so the session is ended with a fault instead of
            // answering measurements from a dead link until the operator stops the run by hand. The connection ladder
            // opens and closes ports on purpose and suppresses this check.
            if (Volatile.Read(ref _handoverStarted) == 1 && !_aligner.IsConnected && !_aligner.SuppressLinkLossNotification)
            {
                Logger.Warning("[MLAstro][Broker] The firmware link is gone while the session is running. Reporting a fault so TPPA cancels the run.");
                _ = EndSessionForLostLinkAsync();
                return;
            }

            var capabilities = _client.Capabilities;
            var silenceLimitMs = capabilities?.SilenceTimeoutMs > 0
                ? Math.Max(10000, capabilities.SilenceTimeoutMs)
                : BridgeSilenceFallbackMs;

            var silenceMs = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - Volatile.Read(ref _lastInboundTicks)).TotalMilliseconds;
            if (silenceMs <= silenceLimitMs) { return; }

            Logger.Warning($"[MLAstro][Broker] TPPA has been silent for {silenceMs / 1000:0} s with a session open. Abandoning the session.");
            _ = AbandonSilentSessionAsync();
        }

        private async Task AbandonSilentSessionAsync()
        {
            await AbortSessionAsync(BridgeReason.ExternalLost).ConfigureAwait(false);
            SetStatus("TPPA stopped answering");
        }

        /// <summary>
        /// Ends the session when the firmware link disappeared after the axes were handed over: a fault is the only
        /// honest answer, because the controller can neither move nor verify them any more. TPPA ends the run with it
        /// and the operator reconnects and starts again.
        /// </summary>
        private async Task EndSessionForLostLinkAsync()
        {
            await PublishFaultAndEndAsync(BridgeReason.FirmwareDisconnected,
                                          "The link to the alignment hardware was lost during the session.").ConfigureAwait(false);
        }

        private void ResetSessionState()
        {
            lock (_gate)
            {
                _unusableMeasurements = 0;
                _moveCounter = 0;
                _windowId = null;
                _lastMeasurement = null;
                _paused = false;
                _resumeNeedsMeasurement = false;
                _stopping = false;
                // A new session is not handed over yet: a manual STOP stays ignored until the reference sweep ends.
                Volatile.Write(ref _handoverStarted, 0);
                Volatile.Write(ref _pausedBeforeCorrection, 0);
                // The direction detection starts from scratch in every session.
                for (var i = 0; i < 2; i++)
                {
                    _axisReferenceErrorArcMin[i] = null;
                    _axisIncreaseStreak[i] = 0;
                    _axisAutoDirectionChanges[i] = 0;
                    _axisDirectionConfirmed[i] = false;
                }

                // Codes collected for an error cancel never survive into the next session.
                lock (_pendingDriverErrorCodes) { _pendingDriverErrorCodes.Clear(); }
            }
        }

        private void ClearWindow()
        {
            lock (_gate)
            {
                _windowId = null;
            }
        }

        private void CancelSessionInternal()
        {
            StopKeepAlive();
            _watchdogTimer.Stop();

            CancellationTokenSource cts;
            lock (_gate)
            {
                cts = _sessionCts;
                _sessionCts = null;
                _windowId = null;
                _windowGrant = null;
                _paused = false;
                _resumeNeedsMeasurement = false;
            }

            try { cts?.Cancel(); } catch (ObjectDisposedException) { }
            cts?.Dispose();

            if (cts != null) { NotifySessionActive(false); }
        }

        private void NotifySessionActive(bool active)
        {
            try { SessionActiveChanged?.Invoke(this, active); } catch (Exception ex) { Logger.Error(ex); }
        }

        /// <summary>
        /// SIGNED error of each axis (0 = AZ, 1 = ALT) when the three reference points were solved, re-anchored
        /// every time the axis crosses its target. The sign is the side of the target the error sits on, so it is
        /// what tells a wrong direction from a right one; the magnitude only says whether the axis improved.
        /// </summary>
        private readonly double?[] _axisReferenceErrorArcMin = new double?[2];

        /// <summary>Consecutive measurements that kept the reference side of the target and grew (any improvement or crossing resets it).</summary>
        private readonly int[] _axisIncreaseStreak = new int[2];

        /// <summary>
        /// True when the last executed step really commanded that axis. Growth of an axis that was left alone (held
        /// inside the tolerance, or a verify-only step) says nothing about its direction: that error moves with the
        /// sky, the mount or the tripod, not with the command.
        /// </summary>
        private readonly bool[] _axisCommandedLastStep = new bool[2];

        /// <summary>Last judged error of each axis (arcmin), used by the messages of the growth verdict.</summary>
        private readonly double[] _axisLastErrorArcMin = new double[2];

        /// <summary>Automatic direction changes already used per axis in this session (one is allowed).</summary>
        private readonly int[] _axisAutoDirectionChanges = new int[2];

        /// <summary>
        /// True once that axis showed an improvement after a move, which is what proves its direction: until then
        /// the moves of that axis are the direction probe and TPPA shows "detecting direction" instead of "adjusting".
        /// </summary>
        private readonly bool[] _axisDirectionConfirmed = new bool[2];

        /// <summary>
        /// Forgets everything the interrupted step knew about the axes: an axis counts as not commanded, so the next
        /// measurement is not judged as if a move had completed, and the growth streak of a probe that was cut short
        /// does not count towards a wrong direction. The sweep anchor is kept - it belongs to the session, not to the
        /// step - and the next move is planned from the new measurement alone, never from the travel that was stopped.
        /// </summary>
        private void ResetDirectionTrackingAfterInterruption()
        {
            _axisCommandedLastStep[0] = false;
            _axisCommandedLastStep[1] = false;
            _axisIncreaseStreak[0] = 0;
            _axisIncreaseStreak[1] = 0;
        }

        /// <summary>
        /// True while the given plan still probes a direction: it moves an axis whose direction has not been
        /// proven by an improvement yet.
        /// </summary>
        private bool PlanIsDetectingDirection(CorrectionPlan plan)
        {
            if (plan == null) { return false; }
            return (plan.MoveAzimuth && !_axisDirectionConfirmed[0])
                   || (plan.MoveAltitude && !_axisDirectionConfirmed[1]);
        }

        /// <summary>
        /// Follows one axis over the measurements. The DIRECTION is judged on the SIGN of the error against the
        /// reference sweep, the MAGNITUDE only says whether the axis improved: a move in the right direction brings
        /// the error down on the same side of the target and - with the overshoot - takes it all the way past the
        /// target, so the next measurement comes back with the OPPOSITE sign of the sweep error. A move in the wrong
        /// direction keeps the sign of the sweep error and grows there. Two consecutive measurements that keep the
        /// sweep sign while growing mean the direction of that axis is wrong, so the axis name is returned; crossing
        /// the target confirms the direction and moves the reference to the new side. Inside the tolerance nothing is
        /// judged: the sign is only solve noise there.
        /// </summary>
        private string TrackAxisError(string axisName, int index, double errorArcMin, double toleranceArcMin)
        {
            var reference = _axisReferenceErrorArcMin[index];
            if (!reference.HasValue)
            {
                // The reference sweep result of this axis is the anchor of the whole session: every later
                // measurement is judged against it, never against the measurement before it.
                _axisReferenceErrorArcMin[index] = errorArcMin;
                _axisIncreaseStreak[index] = 0;
                return null;
            }

            if (Math.Abs(errorArcMin) <= toleranceArcMin)
            {
                // Inside the tolerance the sign is meaningless (solve noise may sit on either side of the target).
                _axisIncreaseStreak[index] = 0;
                return null;
            }

            // An axis that was not commanded cannot be judged: its error moves with the sky, the mount or the tripod.
            if (!_axisCommandedLastStep[index]) { return null; }

            _axisLastErrorArcMin[index] = errorArcMin;

            if (Math.Sign(errorArcMin) != Math.Sign(reference.Value))
            {
                // The axis crossed its target, which only a move in the commanded direction can do - the overshoot
                // leg does it on purpose. The direction is proven and the reference follows the axis to the new side.
                _axisIncreaseStreak[index] = 0;
                if (!_axisDirectionConfirmed[index])
                {
                    _axisDirectionConfirmed[index] = true;
                    Logger.Info($"[MLAstro][Broker] The {axisName} direction is confirmed: the error crossed the target " +
                                $"({reference.Value:0.##}' -> {errorArcMin:0.##}').");
                }

                _axisReferenceErrorArcMin[index] = errorArcMin;
                return null;
            }

            if (Math.Abs(errorArcMin) > Math.Abs(reference.Value))
            {
                _axisIncreaseStreak[index]++;
                Logger.Warning($"[MLAstro][Broker] The {axisName} error stayed on the reference side and grew " +
                               $"({reference.Value:0.##}' -> {errorArcMin:0.##}'), check {_axisIncreaseStreak[index]}/{MaxWorseningMeasurements}.");
                SetStatus($"{axisName} error grew ({_axisIncreaseStreak[index]}/{MaxWorseningMeasurements}): {reference.Value:0.##}' -> {errorArcMin:0.##}'");

                return _axisIncreaseStreak[index] >= MaxWorseningMeasurements ? axisName : null;
            }

            if (!_axisDirectionConfirmed[index])
            {
                // Same side of the target, smaller error: the moves of this axis go the right way.
                _axisDirectionConfirmed[index] = true;
                Logger.Info($"[MLAstro][Broker] The {axisName} direction is confirmed ({reference.Value:0.##}' -> {errorArcMin:0.##}').");
            }

            _axisIncreaseStreak[index] = 0;
            return null;
        }

        /// <summary>
        /// One axis kept growing on the reference side. While that axis is still probing its direction this is the
        /// direction verdict: auto mode flips the stored software reverse direction once, and without a flip the
        /// session is cancelled with the matching hint. Once the direction has been proven (an improvement or a target
        /// crossing was seen), a growing error is NOT a direction problem any more: the axis only gets a warning while
        /// it keeps growing, and the session is stopped after <see cref="MaxConfirmedGrowthMeasurements"/> growing
        /// measurements in a row, with a note that states what was measured instead of blaming the direction.
        /// Returns true when the session was ended.
        /// </summary>
        private async Task<bool> HandleAxisGrowthAsync(string axisName, int index)
        {
            if (_axisDirectionConfirmed[index])
            {
                var referenceArcMin = _axisReferenceErrorArcMin[index];
                var errorArcMin = _axisLastErrorArcMin[index];

                if (_axisIncreaseStreak[index] < MaxConfirmedGrowthMeasurements)
                {
                    // The direction of this axis is proven, so the growth is not a direction problem: warn, keep
                    // correcting, and only stop when the error keeps growing anyway.
                    var warning = $"The {axisName} polar error grew on the side of the reference sweep value " +
                                  $"({referenceArcMin:0.##}' -> {errorArcMin:0.##}') after {_axisIncreaseStreak[index]} " +
                                  "corrections in a row - the correction goes on.";
                    Logger.Warning($"[MLAstro][Broker] {warning}");
                    SetStatus($"{axisName} is not converging ({errorArcMin:0.##}')");
                    try
                    {
                        NINA.Core.Utility.Notification.Notification.ShowWarning(warning, TimeSpan.FromMinutes(1));
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"[MLAstro][Broker] Could not show the growth warning: {ex.Message}");
                    }

                    return false;
                }

                var stopNote = $"The {axisName} polar error kept growing on the side of the reference sweep value " +
                               $"({referenceArcMin:0.##}' -> {errorArcMin:0.##}') for {_axisIncreaseStreak[index]} consecutive " +
                               "corrections, so the session was stopped." + Environment.NewLine +
                               $"The {axisName} direction was already confirmed by an improvement in this session.";
                Logger.Error($"[MLAstro][Broker] {axisName} is not converging: {stopNote}");
                await AbortSessionAsync(BridgeReason.ControllerFault, stopNote).ConfigureAwait(false);
                return true;
            }

            var mayFlip = _settings.CorrectionAutoChangeDirection
                          && _axisAutoDirectionChanges[index] == 0;

            if (mayFlip)
            {
                _axisAutoDirectionChanges[index] = 1;
                if (index == 0)
                {
                    _settings.SoftwareReverseAzimuth = !_settings.SoftwareReverseAzimuth;
                }
                else
                {
                    _settings.SoftwareReverseAltitude = !_settings.SoftwareReverseAltitude;
                }

                // The new direction is unproven again, so the next moves probe it once more. The reference stays
                // the sweep value of the session: after the flip the same fixed reference has to be beaten.
                _axisDirectionConfirmed[index] = false;
                _axisIncreaseStreak[index] = 0;

                var flipText = $"Wrong direction detected. Auto changing direction ({axisName}).";
                Logger.Warning($"[MLAstro][Broker] {flipText}");
                SetStatus(flipText);
                try
                {
                    NINA.Core.Utility.Notification.Notification.ShowWarning(
                        flipText + Environment.NewLine +
                        $"The {axisName} software direction was flipped" +
                        "The alignment continues with the new direction.",
                        TimeSpan.FromMinutes(1));
                }
                catch (Exception ex)
                {
                    Logger.Warning($"[MLAstro][Broker] Could not show the direction notification: {ex.Message}");
                }

                return false;
            }

            var guidance = _settings.CorrectionAutoChangeDirection
                ? _axisDirectionConfirmed[index]
                    ? $"The {axisName} direction was already confirmed by an improvement in this session, so it is not changed automatically. Check the axis, then run the polar alignment again."
                    : $"The {axisName} direction was already changed once in this session and the error still grows, so a direction setting is probably not the cause."
                : $"Change it by hand: MLAstroRPA -> SOFTWARE SETTING -> turn on \"Reverse {(index == 0 ? "Azimuth" : "Altitude")} direction (software)\", then run the polar alignment again.";

            var note = $"The {axisName} polar error stayed on the side of the reference sweep value " +
                       $"({_axisReferenceErrorArcMin[index]:0.##}') and grew after two corrections in a row." + Environment.NewLine +
                       guidance;
            Logger.Error($"[MLAstro][Broker] Wrong direction detected on {axisName}: {note}");
            await AbortSessionAsync(BridgeReason.ControllerFault, note).ConfigureAwait(false);
            return true;
        }

        private async Task PublishFaultAndEndAsync(string reason, string detail)
        {
            await _aligner.StopAsync().ConfigureAwait(false);

            await _client.PublishAsync(BridgeKind.Fault,
                                       SessionId,
                                       new ControllerFaultPayload
                                       {
                                           Reason = reason,
                                           Detail = detail,
                                           HardwareStopStatus = _aligner.IsConnected
                                               ? BridgeHardwareStopStatus.Ok
                                               : BridgeHardwareStopStatus.Unknown
                                       }).ConfigureAwait(false);

            SetStatus($"Fault: {detail}");
            CancelSessionInternal();
        }

        private void SetStatus(string status)
        {
            var changed = false;
            lock (_gate)
            {
                if (!string.Equals(_statusText, status, StringComparison.Ordinal))
                {
                    _statusText = status;
                    changed = true;
                }
            }

            if (!changed) { return; }

            try { StatusChanged?.Invoke(this, EventArgs.Empty); } catch (Exception ex) { Logger.Error(ex); }
        }

        public void Dispose()
        {
            if (_disposed) { return; }
            _disposed = true;
            Stop();
            _keepAliveTimer.Dispose();
            _watchdogTimer.Dispose();
            _queueSignal.Dispose();
        }
    }
}
