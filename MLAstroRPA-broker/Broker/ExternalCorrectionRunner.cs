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
    public sealed class ExternalCorrectionRunner : IDisposable
    {
        /// <summary>How long we wait for TPPA to grant a capture window after asking for it.</summary>
        private const int WindowGrantTimeoutMs = 15000;

        /// <summary>Number of unusable (unstable / failed) measurements before the session is cancelled.</summary>
        private const int MaxUnusableMeasurements = 5;

        /// <summary>Settle time between the overshoot move and the back-off move.</summary>
        private const int OvershootSettleMs = 1000;

        private readonly TppaBrokerClient _client;
        private readonly HardwareAligner _aligner;
        private readonly PluginSettings _settings;

        private readonly ConcurrentQueue<ExternalCorrectionEnvelope> _queue = new ConcurrentQueue<ExternalCorrectionEnvelope>();
        private readonly SemaphoreSlim _queueSignal = new SemaphoreSlim(0);
        private readonly object _gate = new object();
        private readonly System.Timers.Timer _keepAliveTimer;
        private readonly System.Timers.Timer _sessionTimeoutTimer;

        private CancellationTokenSource _sessionCts;
        private Task _worker;
        private bool _running;
        private bool _disposed;

        private string _sessionId;
        private string _windowId;
        private TaskCompletionSource<TppaAdjustmentGrant> _windowGrant;
        private TppaMeasurement _lastMeasurement;
        private int _consecutiveBelowTolerance;
        private int _unusableMeasurements;
        private int _moveCounter;
        private string _statusText = "Idle";
        private bool _paused;
        private bool _resumeNeedsMeasurement;

        /// <summary>True while TPPA reported that the operator paused the run.</summary>
        private bool IsPaused
        {
            get { lock (_gate) { return _paused; } }
        }

        public ExternalCorrectionRunner(TppaBrokerClient client, HardwareAligner aligner, PluginSettings settings)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _aligner = aligner ?? throw new ArgumentNullException(nameof(aligner));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));

            _keepAliveTimer = new System.Timers.Timer(2000) { AutoReset = true };
            _keepAliveTimer.Elapsed += OnKeepAliveTick;

            _sessionTimeoutTimer = new System.Timers.Timer(1000) { AutoReset = false };
            _sessionTimeoutTimer.Elapsed += OnSessionTimeoutTick;
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
            ExternalCorrectionEnvelope sessionEnvelope;
            lock (_gate)
            {
                sessionEnvelope = _sessionId == null
                    ? null
                    : ExternalCorrectionEnvelope.Create(TppaBrokerKind.Cancel,
                                                        _sessionId,
                                                        Guid.NewGuid().ToString("N"),
                                                        null,
                                                        0,
                                                        TppaBrokerContract.TppaRecipient,
                                                        new ControllerCancelPayload { Reason = reason });
            }

            CancelSessionInternal();
            await _aligner.StopAsync().ConfigureAwait(false);

            if (sessionEnvelope != null)
            {
                await _client.PublishAsync(TppaBrokerKind.Cancel,
                                           sessionEnvelope.SessionId,
                                           sessionEnvelope.Payload.ToObject<ControllerCancelPayload>())
                             .ConfigureAwait(false);
            }

            SetStatus("Idle");
        }

        // ===== inbound =====

        private void OnEnvelopeReceived(object sender, ExternalCorrectionEnvelope envelope)
        {
            if (envelope == null || !_running) { return; }

            switch (envelope.Kind)
            {
                case TppaBrokerKind.AdjustmentGranted:
                    HandleAdjustmentGranted(envelope);
                    return;

                case TppaBrokerKind.StopRequested:
                    // Answer immediately: the alignment loop may be minutes deep inside a move.
                    _ = HandleStopRequestAsync(envelope);
                    return;

                case TppaBrokerKind.PauseRequested:
                    // Same reason: a pause has to stop the axes while a move is running, so it must not
                    // wait behind the worker queue.
                    _ = HandlePauseRequestAsync(envelope);
                    return;

                case TppaBrokerKind.SessionEnded:
                    CancelSessionInternal();
                    Enqueue(envelope);
                    return;

                default:
                    Enqueue(envelope);
                    return;
            }
        }

        private void Enqueue(ExternalCorrectionEnvelope envelope)
        {
            _queue.Enqueue(envelope);
            try { _queueSignal.Release(); } catch (SemaphoreFullException) { }
        }

        private void HandleAdjustmentGranted(ExternalCorrectionEnvelope envelope)
        {
            TaskCompletionSource<TppaAdjustmentGrant> pending;
            lock (_gate)
            {
                pending = _windowGrant;
            }

            var grant = envelope.PayloadAs<TppaAdjustmentGrant>();
            if (grant == null) { return; }

            lock (_gate)
            {
                _windowId = grant.WindowId;
            }

            pending?.TrySetResult(grant);
            StartKeepAlive(grant);
        }

        private async Task HandleStopRequestAsync(ExternalCorrectionEnvelope envelope)
        {
            var request = envelope.PayloadAs<TppaStopRequest>();
            var reason = request?.Reason ?? TppaBrokerReason.UserStop;
            Logger.Info($"[MLAstro][Broker] TPPA requested a stop: {reason}.");

            CancelSessionInternal();

            var stopStatus = TppaHardwareStopStatus.Ok;
            try
            {
                // The operator asked for a stop: STOP goes to the firmware unconditionally, and the move
                // that is waiting for its completion token is released at the same time.
                _aligner.AbortPendingMove();
                if (_aligner.IsConnected)
                {
                    await _aligner.StopAsync().ConfigureAwait(false);
                }
                else
                {
                    stopStatus = TppaHardwareStopStatus.Unknown;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[MLAstro][Broker] Stop failed: {ex.Message}");
                stopStatus = TppaHardwareStopStatus.Fault;
            }

            await _client.PublishAsync(TppaBrokerKind.Stopped,
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
        private async Task HandlePauseRequestAsync(ExternalCorrectionEnvelope envelope)
        {
            var request = envelope.PayloadAs<TppaPauseRequest>();
            var paused = request?.Paused == true;
            var reason = request?.Reason ?? (paused ? TppaBrokerReason.Paused : TppaBrokerReason.Resumed);

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
                    var stopped = await _aligner.StopMoveAsync().ConfigureAwait(false);
                    if (stopped)
                    {
                        // The move we were running is gone: TPPA expects a fresh request after the resume.
                        lock (_gate)
                        {
                            _resumeNeedsMeasurement = true;
                        }
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

            await _client.PublishAsync(TppaBrokerKind.RequestMeasurement,
                                       sessionId,
                                       new TppaMeasurementRequest
                                       {
                                           WindowId = null,
                                           StationaryAndSettled = true,
                                           Reason = TppaBrokerReason.Resumed
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

        private async Task HandleEnvelopeAsync(ExternalCorrectionEnvelope envelope)
        {
            switch (envelope.Kind)
            {
                case TppaBrokerKind.SessionState:
                    await HandleSessionStateAsync(envelope).ConfigureAwait(false);
                    return;

                case TppaBrokerKind.Measurement:
                    await HandleMeasurementAsync(envelope).ConfigureAwait(false);
                    return;

                case TppaBrokerKind.SessionEnded:
                    HandleSessionEnded(envelope);
                    return;

                case TppaBrokerKind.Fault:
                    Logger.Warning("[MLAstro][Broker] TPPA reported a fault.");
                    return;

                default:
                    Logger.Debug($"[MLAstro][Broker] Ignoring '{envelope.Kind}'.");
                    return;
            }
        }

        private async Task HandleSessionStateAsync(ExternalCorrectionEnvelope envelope)
        {
            var state = envelope.PayloadAs<TppaSessionState>();
            if (state == null) { return; }

            Logger.Debug($"[MLAstro][Broker] TPPA session state: {state.State} ({state.Reason}).");

            if (string.Equals(state.State, TppaBrokerState.Preparing, StringComparison.Ordinal))
            {
                await HandleSessionPreparingAsync(envelope.SessionId).ConfigureAwait(false);
                return;
            }

            if (string.Equals(state.State, TppaBrokerState.Ended, StringComparison.Ordinal))
            {
                HandleSessionEnded(envelope);
                return;
            }

            SetStatus($"TPPA session: {state.State}");

            if (string.Equals(state.Reason, TppaBrokerReason.SilenceTimeout, StringComparison.Ordinal))
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

            ExternalCorrectionSettings.FromPluginSettings(_settings);

            if (!_aligner.IsConnected)
            {
                Logger.Warning("[MLAstro][Broker] TPPA started a session but the hardware is not connected. Reporting a fault.");
                SetStatus("Hardware not connected");
                await _client.PublishAsync(TppaBrokerKind.Fault,
                                           sessionId,
                                           new ControllerFaultPayload
                                           {
                                               Reason = TppaBrokerReason.ControllerFault,
                                               Detail = "The MLAstro hardware link is not connected.",
                                               HardwareStopStatus = TppaHardwareStopStatus.Unknown
                                           }).ConfigureAwait(false);
                return;
            }

            ResetSessionState();
            SetStatus("TPPA session: controller ready");

            await _client.PublishAsync(TppaBrokerKind.ControllerReady,
                                       sessionId,
                                       new ControllerReadyPayload
                                       {
                                           Controller = TppaBrokerContract.ControllerName,
                                           ControllerVersion = typeof(ExternalCorrectionRunner).Assembly.GetName().Version?.ToString(),
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

            var settings = ExternalCorrectionSettings.FromPluginSettings(_settings);
            _sessionTimeoutTimer.Interval = Math.Max(30, settings.TimeoutSec) * 1000.0;
            _sessionTimeoutTimer.Start();

            Logger.Info($"[MLAstro][Broker] Session {SessionId} started. Session limit {settings.TimeoutSec} s.");
            _ = cts;
            NotifySessionActive(true);
        }

        private void HandleSessionEnded(ExternalCorrectionEnvelope envelope)
        {
            var ended = envelope.PayloadAs<TppaSessionEnded>();
            Logger.Info($"[MLAstro][Broker] Session ended. Reason: {ended?.Reason}; achieved: {ended?.Achieved}; " +
                        $"final total error: {ended?.TotalErrorArcMin:0.##}' (tolerance {ended?.ToleranceUsedArcMin:0.##}').");

            CancelSessionInternal();
            SetStatus(ended?.Achieved == true
                          ? $"Completed: {ended.TotalErrorArcMin:0.##}'"
                          : $"Session ended ({ended?.Reason ?? "unknown"})");
        }

        private async Task HandleMeasurementAsync(ExternalCorrectionEnvelope envelope)
        {
            var measurement = envelope.PayloadAs<TppaMeasurement>();
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

            var settings = ExternalCorrectionSettings.FromPluginSettings(_settings);
            var warnings = new List<string>();
            var plan = ExternalCorrectionEngine.CreatePlan(measurement, settings, warnings);
            foreach (var warning in warnings.Where(w => w != null).Distinct())
            {
                Logger.Warning($"[MLAstro][Broker] {warning}");
            }

            if (token.IsCancellationRequested) { return; }

            if (plan.VerifyOnly)
            {
                await HandleVerifyOnlyAsync(measurement, settings, token).ConfigureAwait(false);
                return;
            }

            ResetConsecutiveBelowTolerance();
            SetStatus($"Adjusting: {plan.Reason}");
            await ExecutePlanAsync(measurement, plan, settings, token).ConfigureAwait(false);
        }

        private async Task HandleVerifyOnlyAsync(TppaMeasurement measurement, ExternalCorrectionSettings settings, CancellationToken token)
        {
            if (measurement.ToleranceReached || measurement.AutoFinishConditionMet)
            {
                _consecutiveBelowTolerance++;
                SetStatus($"Within tolerance ({measurement.TotalErrorArcMin:0.##}') - confirming");

                if (measurement.AutoFinishConditionMet || _consecutiveBelowTolerance >= settings.ConsecutiveToFinish)
                {
                    Logger.Info("[MLAstro][Broker] Alignment is within tolerance. Asking TPPA to complete the session.");
                    await _client.PublishAsync(TppaBrokerKind.RequestCompletion,
                                               SessionId,
                                               new TppaCompletionRequest
                                               {
                                                   WindowId = null,
                                                   Reason = TppaBrokerReason.CompletionRequested,
                                                   ConsecutiveBelowTolerance = _consecutiveBelowTolerance
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
                    await PublishFaultAndEndAsync(TppaBrokerReason.CaptureFailed,
                                                  $"The polar alignment measurement stayed unusable for {MaxUnusableMeasurements} attempts.")
                                 .ConfigureAwait(false);
                    return;
                }
            }

            if (token.IsCancellationRequested) { return; }

            // Ask for a new measurement without moving: either to confirm a passing sample or to recover
            // from an unusable one.
            await _client.PublishAsync(TppaBrokerKind.RequestMeasurement,
                                       SessionId,
                                       new TppaMeasurementRequest
                                       {
                                           WindowId = null,
                                           StationaryAndSettled = true,
                                           Reason = TppaBrokerReason.VerifyOnly
                                       }).ConfigureAwait(false);
            SetStatus("Waiting for the next measurement");
        }

        private async Task ExecutePlanAsync(TppaMeasurement measurement,
                                            CorrectionPlan plan,
                                            ExternalCorrectionSettings settings,
                                            CancellationToken token)
        {
            _moveCounter++;
            var windowId = await RequestWindowAsync(measurement, plan, token).ConfigureAwait(false);
            if (windowId == null)
            {
                Logger.Warning("[MLAstro][Broker] TPPA did not grant a capture window in time. Not moving.");
                SetStatus("Capture window not granted");
                return;
            }

            var aligned = await _aligner.AlignAsync(plan, token).ConfigureAwait(false);
            if (!aligned)
            {
                if (token.IsCancellationRequested)
                {
                    Logger.Info("[MLAstro][Broker] Move aborted because the session was cancelled.");
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

                await PublishFaultAndEndAsync(TppaBrokerReason.ControllerFault,
                                              $"The alignment move {_moveCounter} did not complete on the hardware.")
                             .ConfigureAwait(false);
                return;
            }

            if (settings.OvershootEnabled && plan.HasMove)
            {
                var backOff = BuildBackOffPlan(plan, settings);
                if (backOff != null)
                {
                    try
                    {
                        await Task.Delay(OvershootSettleMs, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    Logger.Info($"[MLAstro][Broker] Backing off the overshoot: {backOff.Reason}");
                    var backed = await _aligner.AlignAsync(backOff, token).ConfigureAwait(false);
                    if (!backed)
                    {
                        if (token.IsCancellationRequested) { return; }
                        await PublishFaultAndEndAsync(TppaBrokerReason.ControllerFault,
                                                      $"The overshoot back-off move {_moveCounter} did not complete on the hardware.")
                                     .ConfigureAwait(false);
                        return;
                    }
                }
            }

            StopKeepAlive();
            ClearWindow();

            if (token.IsCancellationRequested) { return; }

            await _client.PublishAsync(TppaBrokerKind.RequestMeasurement,
                                       SessionId,
                                       new TppaMeasurementRequest
                                       {
                                           WindowId = windowId,
                                           StationaryAndSettled = true,
                                           Reason = TppaBrokerReason.StepFinished
                                       }).ConfigureAwait(false);
            SetStatus("Waiting for the next measurement");
        }

        /// <summary>Builds the move that cancels the overshoot and returns to the target.</summary>
        private static CorrectionPlan BuildBackOffPlan(CorrectionPlan plan, ExternalCorrectionSettings settings)
        {
            var overshoot = plan.AltitudeUp ? settings.OvershootUpArcMin : settings.OvershootDownArcMin;
            if (overshoot <= 0) { return null; }

            var backOff = new CorrectionPlan
            {
                HasMove = true,
                ToleranceArcMin = plan.ToleranceArcMin,
                MoveAzimuth = plan.MoveAzimuth,
                MoveAltitude = plan.MoveAltitude,
                AzimuthMagnitudeArcMin = plan.MoveAzimuth ? settings.OvershootUpArcMin : 0,
                AltitudeMagnitudeArcMin = plan.MoveAltitude ? overshoot : 0,
                // Coming back means moving the opposite way.
                AzimuthRight = !plan.AzimuthRight,
                AltitudeUp = !plan.AltitudeUp
            };
            backOff.Reason = $"az {backOff.AzimuthMagnitudeArcMin:0.##}' alt {backOff.AltitudeMagnitudeArcMin:0.##}' (back-off)";
            return backOff.HasMove ? backOff : null;
        }

        private async Task<string> RequestWindowAsync(TppaMeasurement measurement, CorrectionPlan plan, CancellationToken token)
        {
            var grant = new TaskCompletionSource<TppaAdjustmentGrant>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _windowGrant = grant;
            }

            try
            {
                await _client.PublishAsync(TppaBrokerKind.BeginAdjustment,
                                           SessionId,
                                           new TppaAdjustmentRequest
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

        private void StartKeepAlive(TppaAdjustmentGrant grant)
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

            _ = _client.PublishAsync(TppaBrokerKind.KeepAlive,
                                     sessionId,
                                     new TppaKeepAlive { WindowId = windowId, State = "Adjusting" })
                        .ContinueWith(t => Logger.Error(t.Exception), TaskContinuationOptions.OnlyOnFaulted);
        }

        // ===== session bookkeeping =====

        private void OnSessionTimeoutTick(object sender, ElapsedEventArgs e)
        {
            if (!IsSessionRunning) { return; }

            var settings = ExternalCorrectionSettings.FromPluginSettings(_settings);
            Logger.Warning($"[MLAstro][Broker] The session exceeded its {settings.TimeoutSec} s limit. Cancelling it.");
            _ = AbortSessionAsync(TppaBrokerReason.SessionTimeout);
        }

        private void ResetSessionState()
        {
            lock (_gate)
            {
                _consecutiveBelowTolerance = 0;
                _unusableMeasurements = 0;
                _moveCounter = 0;
                _windowId = null;
                _lastMeasurement = null;
                _paused = false;
                _resumeNeedsMeasurement = false;
            }
            ExternalCorrectionEngine.ResetBacklashHistory();
        }

        private void ResetConsecutiveBelowTolerance()
        {
            lock (_gate)
            {
                _consecutiveBelowTolerance = 0;
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
            _sessionTimeoutTimer.Stop();

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

        private async Task PublishFaultAndEndAsync(string reason, string detail)
        {
            await _aligner.StopAsync().ConfigureAwait(false);

            await _client.PublishAsync(TppaBrokerKind.Fault,
                                       SessionId,
                                       new ControllerFaultPayload
                                       {
                                           Reason = reason,
                                           Detail = detail,
                                           HardwareStopStatus = _aligner.IsConnected
                                               ? TppaHardwareStopStatus.Ok
                                               : TppaHardwareStopStatus.Unknown
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
            _sessionTimeoutTimer.Dispose();
            _queueSignal.Dispose();
        }
    }
}
