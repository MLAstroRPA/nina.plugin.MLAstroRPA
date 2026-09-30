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
    public sealed class BridgeRunner : IDisposable
    {
        /// <summary>How long we wait for TPPA to grant a capture window after asking for it.</summary>
        private const int WindowGrantTimeoutMs = 15000;

        /// <summary>Number of unusable (unstable / failed) measurements before the session is cancelled.</summary>
        private const int MaxUnusableMeasurements = 5;

        /// <summary>
        /// How many consecutive measurements may come back worse on one axis before the direction of that axis is
        /// declared wrong - the plugin then changes the direction itself or cancels the session.
        /// </summary>
        private const int MaxWorseningMeasurements = 2;

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
        /// True once the hardware link was looked for in this session. TPPA heartbeats the
        /// "reference sweep finished" state while the hand-over prompt is open, so without this a failed
        /// connect would restart the whole COM-port scan again and again.
        /// </summary>
        private bool _hardwareConnectAttempted;

        /// <summary>1 once TPPA finished the three reference points and hands the correction over.</summary>
        private int _handoverStarted;

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

                    if (stopped || windowDropped)
                    {
                        // Whatever we interrupted is gone: TPPA expects a fresh request after the resume.
                        lock (_gate)
                        {
                            _resumeNeedsMeasurement = true;
                        }

                        Logger.Info($"[MLAstro][Broker] Pause handled: stopped={stopped}, dropped pending window={windowDropped}.");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"[MLAstro][Broker] Failed to stop the move for the pause: {ex.Message}");
                }
                return;
            }

            Logger.Info("[MLAstro][Broker] TPPA resumed the run.");

            bool needsMeasurement;
            string sessionId;
            lock (_gate)
            {
                needsMeasurement = _resumeNeedsMeasurement;
                _resumeNeedsMeasurement = false;
                sessionId = _sessionId;
            }

            SetStatus("Waiting for the next measurement");

            if (!needsMeasurement || sessionId == null || !IsSessionRunning) { return; }

            await _client.PublishAsync(BridgeKind.RequestMeasurement,
                                       sessionId,
                                       new BridgeMeasurementRequest
                                       {
                                           WindowId = null,
                                           StationaryAndSettled = true,
                                           Reason = BridgeReason.Resumed
                                       }).ConfigureAwait(false);
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
                // The three reference points are measured: only now are the axes needed. A controller that
                // is still offline brings its hardware up here (selected transport, then the other one
                // once) and reports the result - that report is what TPPA checks before the hand-over.
                if (_hardwareConnectAttempted)
                {
                    // Already looked for the controller in this session: a repeated sweep-finished
                    // heartbeat must not start another scan (the failure was already reported).
                    return;
                }

                _hardwareConnectAttempted = true;
                Logger.Info($"[MLAstro][Broker] TPPA finished the reference sweep. Hardware connected: {_aligner.IsConnected}.");
                SetStatus("Connecting the hardware...");

                if (!await TryConnectHardwareAsync().ConfigureAwait(false))
                {
                    SetStatus("Hardware not connected");
                    await _client.PublishAsync(BridgeKind.Fault,
                                               SessionId,
                                               new ControllerFaultPayload
                                               {
                                                   Reason = BridgeReason.ControllerFault,
                                                   Detail = $"No link to the MLAstro controller over {_settings.TransportMode} or over the fallback transport.",
                                                   HardwareStopStatus = BridgeHardwareStopStatus.Unknown
                                               }).ConfigureAwait(false);
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
        /// Brings the hardware link up when TPPA asks for a session: the selected transport is tried first
        /// (serial scan, or wireless connect) and the other one is tried ONCE as a fallback, so a stale
        /// connection choice does not end the run. Returns false when neither transport comes up.
        /// </summary>
        private async Task<bool> TryConnectHardwareAsync()
        {
            if (_aligner.IsConnected) { return true; }

            var selected = _settings.TransportMode;
            SetStatus($"Connecting over {selected}...");
            if (await TryConnectTransportAsync(selected).ConfigureAwait(false)) { return true; }

            var fallback = selected == MlastroTransportMode.Wireless
                ? MlastroTransportMode.Serial
                : MlastroTransportMode.Wireless;

            Logger.Warning($"[MLAstro][Broker] No link over {selected}. Trying {fallback} once.");
            SetStatus($"No link over {selected} - trying {fallback} once...");
            return await TryConnectTransportAsync(fallback).ConfigureAwait(false);
        }

        private async Task<bool> TryConnectTransportAsync(MlastroTransportMode transport)
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
                // would greet the device a second time. Stored port first, then what the OS lists.
                var candidates = new List<string>();
                if (!string.IsNullOrWhiteSpace(_settings.ComPort))
                {
                    candidates.Add(_settings.ComPort);
                }

                foreach (var enumerated in _aligner.AvailablePorts)
                {
                    if (!candidates.Contains(enumerated, StringComparer.OrdinalIgnoreCase))
                    {
                        candidates.Add(enumerated);
                    }
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
            var ready = _aligner.IsHardwareReady;
            var status = _aligner.DeviceStatus;
            var note = ready
                ? null
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
            _ = cts;
            NotifySessionActive(true);
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

            CancellationToken token;
            lock (_gate)
            {
                if (_sessionCts == null)
                {
                    Logger.Warning("[MLAstro][Broker] Received a measurement without an active session. Ignoring it.");
                    return;
                }
                _sessionId = measurement.SessionId ?? _sessionId;
                _lastMeasurement = measurement;
                token = _sessionCts.Token;
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

            // Wrong direction is detected per axis and on the ABSOLUTE error: the sign only says which way the axis
            // has to move, so a value climbing towards zero is an improvement, not a growing error.
            var toleranceArcMin = Math.Max(0, measurement.ToleranceArcMin);
            var wrongAzimuth = TrackAxisError("AZ", 0, Math.Abs(measurement.AzimuthErrorArcMin), toleranceArcMin);
            if (wrongAzimuth != null && await HandleWrongDirectionAsync(wrongAzimuth, 0).ConfigureAwait(false))
            {
                return;
            }

            var wrongAltitude = TrackAxisError("ALT", 1, Math.Abs(measurement.AltitudeErrorArcMin), toleranceArcMin);
            if (wrongAltitude != null && await HandleWrongDirectionAsync(wrongAltitude, 1).ConfigureAwait(false))
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

            if (token.IsCancellationRequested) { return; }

            if (plan.VerifyOnly)
            {
                await HandleVerifyOnlyAsync(measurement, token).ConfigureAwait(false);
                return;
            }

            // The direction detection watches both axes themselves (see TrackAxisError), so nothing of the
            // measured error has to be remembered here.
            SetStatus($"Adjusting: {plan.Reason}");
            await ExecutePlanAsync(measurement, plan, settings, token).ConfigureAwait(false);
        }

        private async Task HandleVerifyOnlyAsync(BridgeMeasurement measurement, CancellationToken token)
        {
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

            // No back-off move: the overshoot leg already travelled past the target and the next
            // measurement corrects whatever is left, exactly like the MLAstro TPPA plugin did. Coming back
            // on purpose would put the play back into the axis the overshoot just removed.

            StopKeepAlive();
            ClearWindow();

            // The firmware reports the move as completed once the axes reached the target, but the mechanics
            // still need a moment (backlash release, vibration). This is the settle the internal TPPA loop
            // does after its own moves; here the controller owns the move, so it waits before measuring.
            var settleSeconds = _settings.AutomatedAdjustmentSettleTime;
            if (settleSeconds > 0)
            {
                SetStatus($"Settling {settleSeconds:0.#} s");
                await Task.Delay(TimeSpan.FromSeconds(settleSeconds), token).ConfigureAwait(false);
            }

            if (token.IsCancellationRequested) { return; }

            await _client.PublishAsync(BridgeKind.RequestMeasurement,
                                       SessionId,
                                       new BridgeMeasurementRequest
                                       {
                                           WindowId = windowId,
                                           StationaryAndSettled = true,
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
                _hardwareConnectAttempted = false;
                // A new session is not handed over yet: a manual STOP stays ignored until the reference sweep ends.
                Volatile.Write(ref _handoverStarted, 0);
                // The direction detection starts from scratch in every session.
                for (var i = 0; i < 2; i++)
                {
                    _axisPreviousErrorArcMin[i] = null;
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

        /// <summary>Measured error of each axis (0 = AZ, 1 = ALT) from the previous measurement.</summary>
        private readonly double?[] _axisPreviousErrorArcMin = new double?[2];

        /// <summary>Consecutive measurements in which that axis got worse (any improvement resets it).</summary>
        private readonly int[] _axisIncreaseStreak = new int[2];

        /// <summary>Automatic direction changes already used per axis in this session (one is allowed).</summary>
        private readonly int[] _axisAutoDirectionChanges = new int[2];

        /// <summary>
        /// True once that axis showed an improvement after a move, which is what proves its direction: until then
        /// the moves of that axis are the direction probe and TPPA shows "detecting direction" instead of "adjusting".
        /// </summary>
        private readonly bool[] _axisDirectionConfirmed = new bool[2];

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
        /// Follows one axis over the measurements, always on the ABSOLUTE error. Two consecutive measurements with a
        /// LARGER magnitude mean the moves of that axis run the wrong way, so the axis name is returned - null while
        /// the direction looks right (an improvement confirms it) or is not decided yet. Inside the tolerance nothing
        /// is judged: the value is solve noise there and may sit on either side of zero.
        /// </summary>
        private string TrackAxisError(string axisName, int index, double errorArcMin, double toleranceArcMin)
        {
            var previous = _axisPreviousErrorArcMin[index];
            _axisPreviousErrorArcMin[index] = errorArcMin;

            if (errorArcMin <= toleranceArcMin)
            {
                // The reference is kept up to date, but a value that small never counts as "growing".
                _axisIncreaseStreak[index] = 0;
                return null;
            }

            if (previous.HasValue && errorArcMin > previous.Value)
            {
                _axisIncreaseStreak[index]++;
                Logger.Warning($"[MLAstro][Broker] The {axisName} error grew ({previous.Value:0.##}' -> {errorArcMin:0.##}'), " +
                               $"check {_axisIncreaseStreak[index]}/{MaxWorseningMeasurements}.");
                SetStatus($"{axisName} error grew ({_axisIncreaseStreak[index]}/{MaxWorseningMeasurements}): {previous.Value:0.##}' -> {errorArcMin:0.##}'");

                return _axisIncreaseStreak[index] >= MaxWorseningMeasurements ? axisName : null;
            }

            if (previous.HasValue && errorArcMin < previous.Value && !_axisDirectionConfirmed[index])
            {
                // The move made this axis better: its direction is proven from here on.
                _axisDirectionConfirmed[index] = true;
                Logger.Info($"[MLAstro][Broker] The {axisName} direction is confirmed ({previous.Value:0.##}' -> {errorArcMin:0.##}').");
            }

            _axisIncreaseStreak[index] = 0;
            return null;
        }

        /// <summary>
        /// The direction of one axis is wrong. Auto mode flips the stored software reverse direction, but only while
        /// that axis is still probing its direction: once an improvement has confirmed the direction, a growing error
        /// is not a direction problem any more, so nothing is flipped then. Without a flip the session is cancelled
        /// with the matching hint. Returns true when the session was ended.
        /// </summary>
        private async Task<bool> HandleWrongDirectionAsync(string axisName, int index)
        {
            var mayFlip = _settings.CorrectionAutoChangeDirection
                          && _axisAutoDirectionChanges[index] == 0
                          && !_axisDirectionConfirmed[index];

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

                // The new direction is unproven again, so the next moves probe it once more.
                _axisDirectionConfirmed[index] = false;
                _axisIncreaseStreak[index] = 0;
                _axisPreviousErrorArcMin[index] = null;

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

            var note = $"The {axisName} polar error grew after two corrections in a row." + Environment.NewLine + guidance;
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

        /// <summary>Diagnostic snapshot used by the options page.</summary>
        public string DescribeLastMeasurement()
        {
            lock (_gate)
            {
                if (_lastMeasurement == null) { return "no measurement yet"; }
                return $"#{_lastMeasurement.SampleIndex}: total {_lastMeasurement.TotalErrorArcMin:0.##}' " +
                       $"(az {_lastMeasurement.AzimuthErrorArcMin:0.##}', alt {_lastMeasurement.AltitudeErrorArcMin:0.##}'), " +
                       $"tolerance {_lastMeasurement.ToleranceArcMin:0.##}'";
            }
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
