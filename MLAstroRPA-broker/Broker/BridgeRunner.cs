using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
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
        /// How many consecutive measurements may come back worse before the correction is declared to run
        /// the wrong way and the session is stopped instead of driving the axes further off the target.
        /// </summary>
        private const int MaxWorseningMeasurements = 2;

        /// <summary>A step counts as worse only when the error grows by this fraction of the previous one.</summary>
        private const double WorseErrorFraction = 0.25;

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
        private double _previousTotalErrorArcMin;
        private int _worseningStreak;
        private long _lastInboundTicks;
        private int _unusableMeasurements;
        private int _moveCounter;
        private string _statusText = "Idle";
        private bool _paused;
        private bool _resumeNeedsMeasurement;
        private bool _stopping;

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

        /// <summary>Aborts a running session on purpose (user pressed Stop on the MLAstro side).</summary>
        public async Task AbortSessionAsync(string reason)
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
                                                        new ControllerCancelPayload { Reason = reason });
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

            if (!_aligner.IsConnected)
            {
                Logger.Warning("[MLAstro][Broker] TPPA started a session but the hardware is not connected. Reporting a fault.");
                SetStatus("Hardware not connected");
                await _client.PublishAsync(BridgeKind.Fault,
                                           sessionId,
                                           new ControllerFaultPayload
                                           {
                                               Reason = BridgeReason.ControllerFault,
                                               Detail = "The MLAstro hardware link is not connected.",
                                               HardwareStopStatus = BridgeHardwareStopStatus.Unknown
                                           }).ConfigureAwait(false);
                return;
            }

            ResetSessionState();
            SetStatus("TPPA session: controller ready");

            await _client.PublishAsync(BridgeKind.ControllerReady,
                                       sessionId,
                                       new ControllerReadyPayload
                                       {
                                           Controller = BridgeContract.ControllerName,
                                           ControllerVersion = typeof(BridgeRunner).Assembly.GetName().Version?.ToString(),
                                           HardwareReady = true,
                                           LinkPath = _aligner.LinkDescription
                                       }).ConfigureAwait(false);

            OnSessionStarted();
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

            var totalErrorArcMin = Math.Abs(measurement.TotalErrorArcMin);
            if (IsCorrectionGettingWorse(measurement, totalErrorArcMin))
            {
                _worseningStreak++;
                Logger.Warning($"[MLAstro][Broker] The correction made the error worse " +
                               $"({_previousTotalErrorArcMin:0.##}' -> {totalErrorArcMin:0.##}'), " +
                               $"measurement {_worseningStreak}/{MaxWorseningMeasurements}.");
                SetStatus($"Correction running the wrong way ({_worseningStreak}/{MaxWorseningMeasurements})");

                if (_worseningStreak >= MaxWorseningMeasurements)
                {
                    // Fail-safe: stopping is the only safe answer when moving makes things worse - a wrong
                    // reverse flag or a reversed axis would otherwise drive the mount off with every step.
                    await PublishFaultAndEndAsync(BridgeReason.ControllerFault,
                                                  $"The correction made the polar error worse for {MaxWorseningMeasurements} " +
                                                  "measurements in a row. Check the software reverse direction settings " +
                                                  "(Reverse Azimuth / Altitude) and that the axes really move the way they should.")
                                 .ConfigureAwait(false);
                    return;
                }
            }
            else
            {
                _worseningStreak = 0;
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

            // The next measurement is compared against the error this move was planned from, so the next
            // move can be judged: better, unchanged, or worse.
            _previousTotalErrorArcMin = totalErrorArcMin;
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
                _previousTotalErrorArcMin = 0;
                _worseningStreak = 0;
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
                _previousTotalErrorArcMin = 0;
                _worseningStreak = 0;
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
        /// True when this measurement is clearly worse than the one the last move was planned from, which is
        /// the signature of a correction that runs the wrong way (reversed axis, wrong reverse flag).
        /// Ordinary solve noise stays below the margin and does not count as worse.
        /// </summary>
        private bool IsCorrectionGettingWorse(BridgeMeasurement measurement, double totalErrorArcMin)
        {
            if (_previousTotalErrorArcMin <= 0) { return false; }

            var grown = totalErrorArcMin - _previousTotalErrorArcMin;
            return grown > Math.Max(2 * measurement.ToleranceArcMin, _previousTotalErrorArcMin * WorseErrorFraction);
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
