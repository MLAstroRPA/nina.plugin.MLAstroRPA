using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using MLAstroRPA.Services;
using NINA.Core.Utility;

namespace MLAstroRPA.Broker
{
    /// <summary>
    /// Thin wrapper around the MLAstro hardware link used by the external correction runner. It reuses
    /// the exact command shapes the CONTROL tab uses, so the firmware contract stays in one place:
    /// the direction flags (AzDi / AlDi) travel inside the ALIGN command, together with the magnitudes
    /// and the trigger token.
    /// </summary>
    public sealed class HardwareAligner : IDisposable
    {
        /// <summary>A single ALIGN command may run for a long time; the firmware answers with a completion token.</summary>
        private static readonly TimeSpan AlignTimeout = TimeSpan.FromSeconds(90);

        private readonly SerialConnectionService _serial;
        private readonly object _gate = new object();
        private TaskCompletionSource<string> _pendingCompletion;

        public HardwareAligner(SerialConnectionService serial)
        {
            _serial = serial ?? throw new ArgumentNullException(nameof(serial));
            _serial.CompletionReceived += OnCompletionReceived;
            _serial.TelemetryDataReceived += OnTelemetryDataReceived;
        }

        /// <summary>True when the hardware link is up (serial or wireless).</summary>
        public bool IsConnected => _serial.IsConnected;

        public string LinkDescription => _serial.ConnectionStatus;

        /// <summary>Known COM ports - used when the stored port does not answer.</summary>
        public string[] AvailablePorts => _serial.AvailablePorts;

        /// <summary>
        /// True when the axes are idle so TPPA may take over: the link is up and the firmware reports READY.
        /// An unknown status counts as ready so a missing poll never blocks a run.
        /// </summary>
        public bool IsHardwareReady => _serial.IsDeviceIdle;

        /// <summary>Firmware STATUS token of the last telemetry ("READY", "MOVING", …), empty while unknown.</summary>
        public string DeviceStatus => _serial.DeviceStatus;

        /// <summary>Opens the serial link on the given port (the caller owns the transport decision).</summary>
        public Task<bool> ConnectSerialAsync(string portName, int baudRate)
            => _serial.ConnectAsync(portName, baudRate);

        /// <summary>Sends the handshake and reports whether the firmware answered - the only proof that the right device is on the port.</summary>
        public Task<bool> SendHandshakeAsync() => _serial.SendHandshakeAsync();

        /// <summary>Closes the current link (used to drop a port that opened but did not answer).</summary>
        public void Disconnect() => _serial.Disconnect();

        /// <summary>
        /// Suppresses "link lost" reporting while ports are opened and closed on purpose (see
        /// <see cref="SerialConnectionService.SuppressLinkLossNotification"/>).
        /// </summary>
        public bool SuppressLinkLossNotification
        {
            get => _serial.SuppressLinkLossNotification;
            set => _serial.SuppressLinkLossNotification = value;
        }

        /// <summary>
        /// Waits for the handshake the connect started to be answered. A COM port that opens but has no
        /// controller behind it never reports "OK!", which is how a wrong port is told apart from the right
        /// one - without sending a second handshake that would race with the first.
        /// </summary>
        public async Task<bool> WaitForHandshakeAsync(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (IsHandshakeOk) { return true; }
                await Task.Delay(150).ConfigureAwait(false);
            }

            return IsHandshakeOk;
        }

        private bool IsHandshakeOk => string.Equals(_serial.HandshakeStatus, "OK!", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Raised whenever <see cref="IsHardwareReady"/> changes, so the session can tell TPPA that the
        /// controller became ready or went busy instead of waiting for the next hand-over.
        /// </summary>
        public event EventHandler ReadinessChanged;

        /// <summary>
        /// Raised when the firmware changes its driver error state (edge-triggered, once per error line).
        /// A session cancels immediately when this reports an active error.
        /// </summary>
        public event EventHandler<DriverErrorState> DriverErrorStateChanged
        {
            add => _serial.ErrorStateChanged += value;
            remove => _serial.ErrorStateChanged -= value;
        }

        private bool lastReadiness;

        private void OnTelemetryDataReceived(object sender, TelemetryDataEventArgs e)
        {
            var ready = IsHardwareReady;
            if (ready == lastReadiness)
            {
                return;
            }

            lastReadiness = ready;
            ReadinessChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Moves the axes according to the plan and waits until the firmware reports the move as
        /// completed. Returns false when the move timed out or the link is not usable.
        /// </summary>
        public async Task<bool> AlignAsync(CorrectionPlan plan, CancellationToken token)
        {
            if (plan == null || !plan.HasMove) { return true; }

            if (!_serial.IsConnected)
            {
                Logger.Warning("[MLAstro][Broker] Cannot align: the hardware link is not connected.");
                return false;
            }

            var expectedCompletion = plan.MoveAzimuth && plan.MoveAltitude
                ? "AAll"
                : plan.MoveAzimuth ? "AzAN" : "AlAN";

            var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _pendingCompletion = completion;
            }

            try
            {
                var command = BuildAlignCommand(plan);
                Logger.Info($"[MLAstro][Broker] ALIGN {command.TrimEnd()} (expecting {expectedCompletion})");
                _serial.Send(command);

                var timeout = Task.Delay(AlignTimeout, token);
                var finished = await Task.WhenAny(completion.Task, timeout).ConfigureAwait(false);
                if (finished != completion.Task)
                {
                    token.ThrowIfCancellationRequested();
                    Logger.Warning($"[MLAstro][Broker] ALIGN did not report {expectedCompletion} within {AlignTimeout.TotalSeconds:0} s.");
                    await StopAsync().ConfigureAwait(false);
                    return false;
                }

                string reported;
                try
                {
                    reported = await completion.Task.ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    // Aborted on purpose (pause or stop): the caller decides what happens next.
                    Logger.Warning("[MLAstro][Broker] ALIGN was aborted before the firmware reported completion.");
                    return false;
                }
                Logger.Info($"[MLAstro][Broker] ALIGN completed ({reported}).");
                return true;
            }
            finally
            {
                lock (_gate)
                {
                    _pendingCompletion = null;
                }
            }
        }

        /// <summary>Builds the ALIGN command for the planned axes.</summary>
        internal static string BuildAlignCommand(CorrectionPlan plan)
        {
            var azimuth = ArcMinToDms(plan.AzimuthMagnitudeArcMin);
            var altitude = ArcMinToDms(plan.AltitudeMagnitudeArcMin);
            var azimuthDirection = plan.AzimuthRight ? 1 : 0;
            var altitudeDirection = plan.AltitudeUp ? 1 : 0;

            // The firmware latches the direction together with the error value, so AzDi / AlDi have to
            // travel in the very same line as the magnitudes and the trigger - exactly like the CONTROL
            // tab does it. Sending them on their own after the trigger would move the axes with whatever
            // direction happened to be stored before.
            if (plan.MoveAzimuth && plan.MoveAltitude)
            {
                return $"AzED:{azimuth.Degrees},AzEM:{azimuth.Minutes},AzES:{azimuth.Seconds},AzDi:{azimuthDirection}," +
                       $"AlED:{altitude.Degrees},AlEM:{altitude.Minutes},AlES:{altitude.Seconds},AlDi:{altitudeDirection},AAll:1\n";
            }

            if (plan.MoveAzimuth)
            {
                return $"AzED:{azimuth.Degrees},AzEM:{azimuth.Minutes},AzES:{azimuth.Seconds},AzDi:{azimuthDirection},AzAN:1\n";
            }

            return $"AlED:{altitude.Degrees},AlEM:{altitude.Minutes},AlES:{altitude.Seconds},AlDi:{altitudeDirection},AlAN:1\n";
        }

        /// <summary>Converts a magnitude in arcminutes into the degrees / minutes / seconds the firmware expects.</summary>
        internal static (int Degrees, int Minutes, int Seconds) ArcMinToDms(double arcMinutes)
        {
            if (double.IsNaN(arcMinutes) || double.IsInfinity(arcMinutes) || arcMinutes < 0)
            {
                arcMinutes = 0;
            }

            var totalSeconds = (long)Math.Round(arcMinutes * 60.0, MidpointRounding.AwayFromZero);
            var degrees = (int)(totalSeconds / 3600);
            var remaining = totalSeconds % 3600;
            var minutes = (int)(remaining / 60);
            var seconds = (int)(remaining % 60);
            return (degrees, minutes, seconds);
        }

        /// <summary>True while an ALIGN move is waiting for its firmware completion token.</summary>
        public bool IsMoving
        {
            get { lock (_gate) { return _pendingCompletion != null; } }
        }

        /// <summary>
        /// Releases a caller that is waiting for a completion token without sending anything to the
        /// hardware, so an aborted move does not linger in its 90 s timeout.
        /// </summary>
        public void AbortPendingMove()
        {
            TaskCompletionSource<string> pending;
            lock (_gate) { pending = _pendingCompletion; }
            pending?.TrySetCanceled();
        }

        /// <summary>
        /// Stops the axes only when a move is in progress, for a pause: an idle firmware must not receive
        /// a STOP. Returns true when a stop was actually sent.
        /// </summary>
        public async Task<bool> StopMoveAsync()
        {
            if (!IsMoving) { return false; }
            AbortPendingMove();
            await StopAsync().ConfigureAwait(false);
            return true;
        }

        /// <summary>Sends an immediate stop to the firmware. Returns true when the STOP really went out.</summary>
        public Task<bool> StopAsync()
        {
            try
            {
                if (!_serial.IsConnected)
                {
                    Logger.Warning("[MLAstro][Broker] Cannot send STOP: the hardware link is not connected.");
                    return Task.FromResult(false);
                }

                var sent = _serial.Send("STOP:1\n");
                if (sent)
                {
                    Logger.Info("[MLAstro][Broker] Sent STOP to the hardware.");
                }
                else
                {
                    Logger.Warning("[MLAstro][Broker] STOP was not accepted by the link.");
                }

                return Task.FromResult(sent);
            }
            catch (Exception ex)
            {
                Logger.Error($"[MLAstro][Broker] Failed to stop the hardware: {ex.Message}");
                return Task.FromResult(false);
            }
        }

        /// <summary>Sends an emergency stop to the firmware.</summary>
        public Task EmergencyStopAsync()
        {
            try
            {
                if (_serial.IsConnected)
                {
                    _serial.Send("ESTOP:1\n");
                    Logger.Info("[MLAstro][Broker] Sent ESTOP to the hardware.");
                }
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                Logger.Error($"[MLAstro][Broker] Failed to emergency stop the hardware: {ex.Message}");
                return Task.CompletedTask;
            }
        }

        private void OnCompletionReceived(object sender, string completionType)
        {
            if (string.IsNullOrEmpty(completionType)) { return; }

            TaskCompletionSource<string> pending;
            lock (_gate)
            {
                pending = _pendingCompletion;
            }

            if (pending == null) { return; }
            if (!string.Equals(completionType, "AAll", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(completionType, "AzAN", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(completionType, "AlAN", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            pending.TrySetResult(completionType);
            Logger.Debug(string.Format(CultureInfo.InvariantCulture,
                                       "[MLAstro][Broker] Completion token {0} received.",
                                       completionType));
        }

        public void Dispose()
        {
            _serial.CompletionReceived -= OnCompletionReceived;
            lock (_gate)
            {
                _pendingCompletion?.TrySetCanceled();
                _pendingCompletion = null;
            }
        }
    }
}
