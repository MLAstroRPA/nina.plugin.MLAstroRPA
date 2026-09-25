using System;

namespace MLAstro_Robotic_Polar_Alignment.Broker
{
    /// <summary>
    /// Capabilities published by TPPA in reply to our announcement. Every tunable value used by this
    /// controller is read from here: nothing about the TPPA side is hard coded.
    /// </summary>
    public sealed class TppaCapabilities
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
    public sealed class TppaMeasurement
    {
        public string MeasurementId { get; set; }
        public string SessionId { get; set; }
        public string WindowId { get; set; }
        public int SampleIndex { get; set; }
        public bool IsFirstMeasurement { get; set; }
        public string Status { get; set; }
        public double AzimuthErrorDeg { get; set; }
        public double AltitudeErrorDeg { get; set; }
        public double TotalErrorDeg { get; set; }
        public double AzimuthErrorArcMin { get; set; }
        public double AltitudeErrorArcMin { get; set; }
        public double TotalErrorArcMin { get; set; }
        public double ToleranceArcMin { get; set; }
        public bool ToleranceReached { get; set; }
        public bool AutoFinishConditionMet { get; set; }
        public int ConsecutiveBelowTolerance { get; set; }
        public string AzimuthDirection { get; set; }
        public string AltitudeDirection { get; set; }
        public bool Northern { get; set; }
        public bool ContinuousEstimation { get; set; }
        public DateTimeOffset TimestampUtc { get; set; }

        public bool IsUsable => string.Equals(Status, TppaMeasurementStatus.Valid, StringComparison.OrdinalIgnoreCase);

        public ExternalAzimuthDirection AzimuthDirectionValue =>
            Enum.TryParse<ExternalAzimuthDirection>(AzimuthDirection, out var value) ? value : ExternalAzimuthDirection.None;

        public ExternalAltitudeDirection AltitudeDirectionValue =>
            Enum.TryParse<ExternalAltitudeDirection>(AltitudeDirection, out var value) ? value : ExternalAltitudeDirection.None;
    }

    /// <summary>Payload of <c>BeginAdjustment</c>: we want to hold the capture for a move sequence.</summary>
    public sealed class TppaAdjustmentRequest
    {
        public string MeasurementId { get; set; }
        public double? PlannedAzimuthArcMin { get; set; }
        public double? PlannedAltitudeArcMin { get; set; }
        public string Note { get; set; }
    }

    /// <summary>Payload of <c>AdjustmentGranted</c>: TPPA handed us a capture window.</summary>
    public sealed class TppaAdjustmentGrant
    {
        public string WindowId { get; set; }
        public string MeasurementId { get; set; }
        public long MaxWindowMs { get; set; }
        public int SilenceTimeoutMs { get; set; }
        public DateTimeOffset GrantedAtUtc { get; set; }
    }

    /// <summary>Payload of <c>RequestMeasurement</c>: we want TPPA to take a new measurement.</summary>
    public sealed class TppaMeasurementRequest
    {
        public string WindowId { get; set; }
        public bool StationaryAndSettled { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>Payload of <c>RequestCompletion</c>: we believe the alignment is finished.</summary>
    public sealed class TppaCompletionRequest
    {
        public string WindowId { get; set; }
        public string Reason { get; set; }
        public int ConsecutiveBelowTolerance { get; set; }
    }

    /// <summary>Payload of <c>KeepAlive</c>: proves we are still alive while holding a window.</summary>
    public sealed class TppaKeepAlive
    {
        public string WindowId { get; set; }
        public string State { get; set; }
        public string Note { get; set; }
    }

    /// <summary>Payload of <c>SessionState</c> (TPPA heartbeat and state changes).</summary>
    public sealed class TppaSessionState
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
    public sealed class TppaStopRequest
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
    public sealed class TppaSessionEnded
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
