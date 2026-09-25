using System;

namespace MLAstroRPA.Broker
{
    /// <summary>
    /// Capabilities published by TPPA in reply to our announcement. Every tunable value used by this
    /// controller is read from here: nothing about the TPPA side is hard coded.
    /// </summary>
    public sealed class BridgeCapabilities
    {
        public string Controller { get; set; }
        public string TppaVersion { get; set; }
        public int InterfaceVersion { get; set; }
        public string[] SupportedKinds { get; set; }
        public bool SessionActive { get; set; }
        public string ActiveSessionId { get; set; }
        public double ToleranceArcMin { get; set; }
        public bool AutoFinishConditionAvailable { get; set; }
        public int HeartbeatMs { get; set; }
        public int SilenceTimeoutMs { get; set; }
        public int ReadyTimeoutMs { get; set; }
        public int SessionTimeoutSec { get; set; }
        public int GraceAfterSilenceMs { get; set; }
        public int StopAckTimeoutMs { get; set; }
        public bool ContinuousEstimation { get; set; }

        public bool Supports(string kind)
        {
            if (SupportedKinds == null) { return false; }
            foreach (var candidate in SupportedKinds)
            {
                if (string.Equals(candidate, kind, StringComparison.Ordinal)) { return true; }
            }
            return false;
        }
    }

    /// <summary>Payload we publish to announce ourselves on the command topic.</summary>
    public sealed class ControllerCapabilitiesAnnounce
    {
        public string Controller { get; set; }
        public string ControllerVersion { get; set; }
        public int InterfaceVersion { get; set; }
        public string[] SupportedKinds { get; set; }
        public string SessionId { get; set; }
    }

    /// <summary>Payload we publish once our hardware is connected and the axes are stationary.</summary>
    public sealed class ControllerReadyPayload
    {
        public string Controller { get; set; }
        public string ControllerVersion { get; set; }
        public bool HardwareReady { get; set; }
        public string LinkPath { get; set; }
        public string Note { get; set; }
    }

    /// <summary>One polar-error sample published by TPPA.</summary>
    public sealed class BridgeMeasurement
    {
        public string MeasurementId { get; set; }
        public string SessionId { get; set; }
        public string WindowId { get; set; }
        public int SampleIndex { get; set; }
        public bool IsFirstMeasurement { get; set; }
        public string Status { get; set; }
        public double AzimuthErrorArcMin { get; set; }
        public double AltitudeErrorArcMin { get; set; }
        public double TotalErrorArcMin { get; set; }
        public double ToleranceArcMin { get; set; }
        public bool ToleranceReached { get; set; }
        public bool AutoFinishConditionMet { get; set; }
        public int ConsecutiveBelowTolerance { get; set; }
        public bool Northern { get; set; }
        public bool ContinuousEstimation { get; set; }
        public DateTimeOffset TimestampUtc { get; set; }

        public bool IsUsable => string.Equals(Status, BridgeMeasurementStatus.Valid, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Correction direction derived from the signed error, because TPPA publishes the arcminutes
        /// only: a positive azimuth error always means "move left", a negative one "move right".
        /// </summary>
        public BridgeAzimuthDirection AzimuthDirectionValue =>
            AzimuthErrorArcMin > 0 ? BridgeAzimuthDirection.Left
            : AzimuthErrorArcMin < 0 ? BridgeAzimuthDirection.Right
            : BridgeAzimuthDirection.None;

        /// <summary>
        /// Altitude direction derived from the signed error and the hemisphere: a positive error means
        /// "move down" in the northern hemisphere and "up" in the southern one.
        /// </summary>
        public BridgeAltitudeDirection AltitudeDirectionValue =>
            AltitudeErrorArcMin > 0 ? (Northern ? BridgeAltitudeDirection.Down : BridgeAltitudeDirection.Up)
            : AltitudeErrorArcMin < 0 ? (Northern ? BridgeAltitudeDirection.Up : BridgeAltitudeDirection.Down)
            : BridgeAltitudeDirection.None;
    }

    /// <summary>
    /// Payload of <c>PauseRequested</c>: the operator paused or resumed the run in TPPA. While paused we
    /// must not start a move and have to stop one that is in progress.
    /// </summary>
    public sealed class BridgePauseRequest
    {
        public bool Paused { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>Payload of <c>BeginAdjustment</c>: we want to hold the capture for a move sequence.</summary>
    public sealed class BridgeAdjustmentRequest
    {
        public string MeasurementId { get; set; }
        public double? PlannedAzimuthArcMin { get; set; }
        public double? PlannedAltitudeArcMin { get; set; }
        public string Note { get; set; }
    }

    /// <summary>Payload of <c>AdjustmentGranted</c>: TPPA handed us a capture window.</summary>
    public sealed class BridgeAdjustmentGrant
    {
        public string WindowId { get; set; }
        public string MeasurementId { get; set; }
        public long MaxWindowMs { get; set; }
        public int SilenceTimeoutMs { get; set; }
        public DateTimeOffset GrantedAtUtc { get; set; }
    }

    /// <summary>Payload of <c>RequestMeasurement</c>: we want TPPA to take a new measurement.</summary>
    public sealed class BridgeMeasurementRequest
    {
        public string WindowId { get; set; }
        public bool StationaryAndSettled { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>Payload of <c>RequestCompletion</c>: we believe the alignment is finished.</summary>
    public sealed class BridgeCompletionRequest
    {
        public string WindowId { get; set; }
        public string Reason { get; set; }
        public int ConsecutiveBelowTolerance { get; set; }
    }

    /// <summary>Payload of <c>KeepAlive</c>: proves we are still alive while holding a window.</summary>
    public sealed class BridgeKeepAlive
    {
        public string WindowId { get; set; }
        public string State { get; set; }
        public string Note { get; set; }
    }

    /// <summary>Payload of <c>SessionState</c> (TPPA heartbeat and state changes).</summary>
    public sealed class BridgeSessionState
    {
        public string State { get; set; }
        public string Reason { get; set; }
        public string WindowId { get; set; }
        public long WindowRemainingMs { get; set; }
        public int SamplesTaken { get; set; }
        public string LastMeasurementId { get; set; }
        public double ToleranceArcMin { get; set; }
        public long SessionElapsedMs { get; set; }
    }

    /// <summary>Payload of <c>StopRequested</c>: TPPA wants us to stop and park.</summary>
    public sealed class BridgeStopRequest
    {
        public string Reason { get; set; }
        public int AckTimeoutMs { get; set; }
    }

    /// <summary>Payload of our <c>Stopped</c> acknowledgement.</summary>
    public sealed class ControllerStoppedPayload
    {
        public string Reason { get; set; }
        public string HardwareStopStatus { get; set; }
        public string Detail { get; set; }
    }

    /// <summary>Payload of our <c>Fault</c> notification.</summary>
    public sealed class ControllerFaultPayload
    {
        public string Reason { get; set; }
        public string Detail { get; set; }
        public string HardwareStopStatus { get; set; }
    }

    /// <summary>Payload of our <c>Cancel</c> notification.</summary>
    public sealed class ControllerCancelPayload
    {
        public string Reason { get; set; }
        public string Note { get; set; }
    }

    /// <summary>Payload of the final <c>SessionEnded</c> message.</summary>
    public sealed class BridgeSessionEnded
    {
        public string Reason { get; set; }
        public bool Achieved { get; set; }
        public double AzimuthErrorArcMin { get; set; }
        public double AltitudeErrorArcMin { get; set; }
        public double TotalErrorArcMin { get; set; }
        public double ToleranceUsedArcMin { get; set; }
        public int SamplesUsed { get; set; }
        public string HardwareStopStatus { get; set; }
        public string Detail { get; set; }
    }
}
