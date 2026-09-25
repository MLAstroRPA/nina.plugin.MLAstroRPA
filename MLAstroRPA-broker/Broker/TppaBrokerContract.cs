using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NINA.Core.Model;
using NINA.Plugin.Interfaces;

namespace MLAstroRPA.Broker
{
    /// <summary>
    /// Names and defaults of the external correction protocol, mirrored from the TPPA fork. The two
    /// plugins do not share an assembly, so both keep their own copy of the names: a change here is a
    /// contract change and must be mirrored on the TPPA side.
    /// </summary>
    public static class TppaBrokerContract
    {
        /// <summary>Interface version carried by every message. Bump on an incompatible change.</summary>
        public const int InterfaceVersion = 1;

        /// <summary>Topic carrying messages published by TPPA (this plugin subscribes).</summary>
        public const string EventTopic = "PolarAlignmentPlugin_PolarAlignment_ExternalEvent";

        /// <summary>Topic carrying messages published by this plugin (TPPA subscribes).</summary>
        public const string CommandTopic = "PolarAlignmentPlugin_PolarAlignment_ExternalCommand";

        /// <summary>Value used in <c>IntendedRecipient</c> for TPPA addressed messages.</summary>
        public const string TppaRecipient = "TPPA";

        /// <summary>Value used in <c>IntendedRecipient</c> for controller addressed messages.</summary>
        public const string ControllerRecipient = "MLAstroRPA";

        /// <summary>Name this controller reports in the handshake.</summary>
        public const string ControllerName = "MLAstroRPA";
    }

    /// <summary>Message kinds exchanged on the external correction topics.</summary>
    public static class TppaBrokerKind
    {
        public const string Capabilities = "Capabilities";
        public const string ControllerReady = "ControllerReady";
        public const string Measurement = "Measurement";
        public const string BeginAdjustment = "BeginAdjustment";
        public const string AdjustmentGranted = "AdjustmentGranted";
        public const string RequestMeasurement = "RequestMeasurement";
        public const string RequestCompletion = "RequestCompletion";
        public const string KeepAlive = "KeepAlive";
        public const string PauseRequested = "PauseRequested";
        public const string SessionState = "SessionState";
        public const string StopRequested = "StopRequested";
        public const string Stopped = "Stopped";
        public const string Cancel = "Cancel";
        public const string Fault = "Fault";
        public const string SessionEnded = "SessionEnded";
    }

    /// <summary>Reasons attached to session state, stop, cancel and session ended messages.</summary>
    public static class TppaBrokerReason
    {
        public const string UserStop = "UserStop";
        public const string SequenceCancel = "SequenceCancel";
        public const string WindowClosed = "WindowClosed";
        public const string Disconnect = "Disconnect";
        public const string SilenceTimeout = "SilenceTimeout";
        public const string SessionTimeout = "SessionTimeout";
        public const string StopAckTimeout = "StopAckTimeout";
        public const string ExternalLost = "ExternalLost";
        public const string NoControllerReady = "NoControllerReady";
        public const string ControllerFault = "ControllerFault";
        public const string ControllerCancel = "ControllerCancel";
        public const string CaptureFailed = "CaptureFailed";
        public const string Superseded = "Superseded";
        public const string MeasurementsFinished = "MeasurementsFinished";
        public const string Completed = "Completed";
        public const string StepFinished = "StepFinished";
        public const string VerifyOnly = "VerifyOnly";
        public const string CompletionRequested = "CompletionRequested";
        public const string NotAchieved = "NotAchieved";

        /// <summary>Operator paused the run on the TPPA side: stop a move that is in progress.</summary>
        public const string Paused = "Paused";

        /// <summary>Operator resumed the run: moving and measuring may continue.</summary>
        public const string Resumed = "Resumed";
    }

    /// <summary>Session states reported by TPPA through <c>SessionState</c>.</summary>
    public static class TppaBrokerState
    {
        public const string Preparing = "Preparing";
        public const string Measuring = "Measuring";
        public const string WaitingForRequest = "WaitingForRequest";
        public const string WindowOpen = "WindowOpen";
        public const string Verifying = "Verifying";
        public const string Ended = "Ended";
    }

    /// <summary>Status of a received <c>Measurement</c>.</summary>
    public static class TppaMeasurementStatus
    {
        public const string Valid = "Valid";
        public const string Unstable = "Unstable";
        public const string CaptureFailed = "CaptureFailed";
    }

    /// <summary>Hardware stop outcome reported back to TPPA.</summary>
    public static class TppaHardwareStopStatus
    {
        public const string Ok = "ok";
        public const string Unknown = "unknown";
        public const string Fault = "fault";
    }

    /// <summary>Azimuth correction direction, derived from the sign of the measured azimuth error.</summary>
    public enum ExternalAzimuthDirection
    {
        None,
        Left,
        Right
    }

    /// <summary>Altitude correction direction, derived from the sign of the measured altitude error and the hemisphere.</summary>
    public enum ExternalAltitudeDirection
    {
        None,
        Up,
        Down
    }

    /// <summary>
    /// Wire envelope. Its JSON is the <see cref="IMessage.Content"/> of every message on both external
    /// topics, which keeps the two plugins independent of each other's assemblies.
    /// </summary>
    public sealed class ExternalCorrectionEnvelope
    {
        public int Version { get; set; } = TppaBrokerContract.InterfaceVersion;
        public string SessionId { get; set; }
        public string CommandId { get; set; }
        public string ReplyTo { get; set; }
        public long SequenceNumber { get; set; }
        public string Kind { get; set; }
        public string IntendedRecipient { get; set; }
        public JObject Payload { get; set; }

        public string ToJson() => JsonConvert.SerializeObject(this, Formatting.None);

        public static ExternalCorrectionEnvelope FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) { return null; }
            try
            {
                return JsonConvert.DeserializeObject<ExternalCorrectionEnvelope>(json);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        public T PayloadAs<T>() where T : class
        {
            return Payload?.ToObject<T>();
        }

        public static ExternalCorrectionEnvelope Create(string kind,
                                                        string sessionId,
                                                        string commandId,
                                                        string replyTo,
                                                        long sequenceNumber,
                                                        string recipient,
                                                        object payload)
        {
            return new ExternalCorrectionEnvelope
            {
                Kind = kind,
                SessionId = sessionId,
                CommandId = commandId,
                ReplyTo = replyTo,
                SequenceNumber = sequenceNumber,
                IntendedRecipient = recipient,
                Payload = payload == null ? null : JObject.FromObject(payload)
            };
        }
    }

    /// <summary>Broker message published on the controller -&gt; TPPA topic.</summary>
    public sealed class ExternalCorrectionCommandMessage : IMessage
    {
        private readonly string _json;

        public ExternalCorrectionCommandMessage(ExternalCorrectionEnvelope envelope)
        {
            _json = envelope.ToJson();
        }

        public Guid SenderId => ResolvePluginId();
        public string Sender => "MLAstroRPA";
        public DateTimeOffset SentAt => DateTime.UtcNow;
        public Guid MessageId => Guid.NewGuid();
        public DateTimeOffset? Expiration => null;
        public Guid? CorrelationId => null;
        public int Version => TppaBrokerContract.InterfaceVersion;
        public IDictionary<string, object> CustomHeaders => new Dictionary<string, object>();
        public string Topic => TppaBrokerContract.CommandTopic;
        public object Content => _json;

        private static Guid ResolvePluginId()
        {
            return Guid.TryParse(NINA.Plugins.MLAstroRPA.MLAstroPlugin.PluginId, out var id) ? id : Guid.Empty;
        }
    }

    /// <summary>Broker message published on the TPPA -&gt; controller topic (used by the simulator).</summary>
    public sealed class ExternalCorrectionEventMessage : IMessage
    {
        private readonly string _json;

        public ExternalCorrectionEventMessage(ExternalCorrectionEnvelope envelope)
        {
            _json = envelope.ToJson();
        }

        public Guid SenderId => ResolvePluginId();
        public string Sender => "MLAstroRPA";
        public DateTimeOffset SentAt => DateTime.UtcNow;
        public Guid MessageId => Guid.NewGuid();
        public DateTimeOffset? Expiration => null;
        public Guid? CorrelationId => null;
        public int Version => TppaBrokerContract.InterfaceVersion;
        public IDictionary<string, object> CustomHeaders => new Dictionary<string, object>();
        public string Topic => TppaBrokerContract.EventTopic;
        public object Content => _json;

        private static Guid ResolvePluginId()
        {
            return Guid.TryParse(NINA.Plugins.MLAstroRPA.MLAstroPlugin.PluginId, out var id) ? id : Guid.Empty;
        }
    }
}
