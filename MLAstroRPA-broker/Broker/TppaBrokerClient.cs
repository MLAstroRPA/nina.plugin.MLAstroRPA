using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using MLAstroRPA.Settings;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Plugin.Interfaces;

namespace MLAstroRPA.Broker
{
    /// <summary>
    /// Transport and handshake with the Three Point Polar Alignment plugin. It owns the subscription to
    /// the TPPA event topic, announces this controller until TPPA answers, keeps the last received
    /// capabilities and forwards every envelope to whoever runs the alignment session.
    /// </summary>
    public sealed class TppaBrokerClient : ISubscriber, IDisposable
    {
        /// <summary>
        /// Announce interval. The announce IS the handshake: TPPA treats a controller as present only
        /// while its announcements keep arriving, and it falls back to its normal behaviour when they
        /// stop. That is why the interval is fixed and never slows down.
        /// </summary>
        private const int AnnounceIntervalMs = 5000;

        private readonly IMessageBroker _broker;
        private readonly PluginSettings _settings;
        private readonly object _gate = new object();
        private readonly System.Timers.Timer _announceTimer;

        private long _sequence;
        private bool _subscribed;
        private bool _disposed;
        private TppaCapabilities _capabilities;
        private DateTimeOffset? _lastReplyAt;
        private string _statusText = "off";

        public TppaBrokerClient(IMessageBroker broker, PluginSettings settings)
        {
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _announceTimer = new System.Timers.Timer(AnnounceIntervalMs) { AutoReset = true };
            _announceTimer.Elapsed += OnAnnounceTick;
        }

        /// <summary>Raised whenever <see cref="StatusText"/> changes.</summary>
        public event EventHandler StatusChanged;

        /// <summary>Raised for every envelope received from TPPA.</summary>
        public event EventHandler<ExternalCorrectionEnvelope> EnvelopeReceived;

        /// <summary>
        /// Raised for every envelope sent to or received from TPPA, so the options page can show the
        /// traffic with its direction (TX / RX).
        /// </summary>
        public event EventHandler<BrokerTrafficEventArgs> Traffic;

        /// <summary>True while the user switch is on.</summary>
        public bool Enabled
        {
            get => _settings.TppaBrokerEnabled;
            set
            {
                if (_settings.TppaBrokerEnabled == value) { return; }
                _settings.TppaBrokerEnabled = value;
                if (value) { Start(); } else { Stop(); }
            }
        }

        /// <summary>Capabilities reported by TPPA, or null when TPPA never answered.</summary>
        public TppaCapabilities Capabilities
        {
            get { lock (_gate) { return _capabilities; } }
        }

        /// <summary>True when TPPA answered and the interface version matches.</summary>
        public bool IsTppaAvailable
        {
            get
            {
                lock (_gate)
                {
                    return _capabilities != null
                           && _capabilities.InterfaceVersion == TppaBrokerContract.InterfaceVersion;
                }
            }
        }

        /// <summary>True when TPPA is running an external session right now.</summary>
        public bool IsSessionActive
        {
            get { lock (_gate) { return _capabilities?.SessionActive == true; } }
        }

        public string ActiveSessionId
        {
            get { lock (_gate) { return _capabilities?.ActiveSessionId; } }
        }

        /// <summary>Short status shown in the options page.</summary>
        public string StatusText
        {
            get { lock (_gate) { return _statusText; } }
        }

        public void Start()
        {
            if (_disposed) { return; }

            if (!_subscribed)
            {
                _broker.Subscribe(TppaBrokerContract.EventTopic, this);
                _subscribed = true;
                Logger.Info("[MLAstro][Broker] Subscribed to the polar alignment external topic.");
            }

            _announceTimer.Interval = AnnounceIntervalMs;
            _announceTimer.Start();
            SetStatus("TPPA: searching");
            _ = AnnounceAsync();
        }

        public void Stop()
        {
            _announceTimer.Stop();

            if (_subscribed)
            {
                try
                {
                    _broker.Unsubscribe(TppaBrokerContract.EventTopic, this);
                }
                catch (Exception ex)
                {
                    Logger.Error($"[MLAstro][Broker] Failed to unsubscribe: {ex.Message}");
                }
                _subscribed = false;
            }

            lock (_gate)
            {
                _capabilities = null;
                _lastReplyAt = null;
            }

            SetStatus("TPPA: off");
        }

        /// <summary>Publishes a message on the controller to TPPA topic.</summary>
        public Task PublishAsync(string kind, string sessionId, object payload, string replyTo = null, string commandId = null)
        {
            var envelope = ExternalCorrectionEnvelope.Create(kind,
                                                             sessionId,
                                                             commandId ?? Guid.NewGuid().ToString("N"),
                                                             replyTo,
                                                             Interlocked.Increment(ref _sequence),
                                                             TppaBrokerContract.TppaRecipient,
                                                             payload);
            RaiseTraffic(BrokerLogDirection.Tx, envelope.Kind);
            return _broker.Publish(new ExternalCorrectionCommandMessage(envelope));
        }

        public Task AnnounceAsync()
        {
            var announce = new ControllerCapabilitiesAnnounce
            {
                Controller = TppaBrokerContract.ControllerName,
                ControllerVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
                InterfaceVersion = TppaBrokerContract.InterfaceVersion,
                SupportedKinds = new[]
                {
                    TppaBrokerKind.Capabilities,
                    TppaBrokerKind.ControllerReady,
                    TppaBrokerKind.BeginAdjustment,
                    TppaBrokerKind.RequestMeasurement,
                    TppaBrokerKind.RequestCompletion,
                    TppaBrokerKind.KeepAlive,
                    TppaBrokerKind.Stopped,
                    TppaBrokerKind.Cancel,
                    TppaBrokerKind.Fault
                },
                SessionId = ActiveSessionId
            };

            return PublishAsync(TppaBrokerKind.Capabilities, null, announce);
        }

        public Task OnMessageReceived(IMessage message)
        {
            if (message == null || _disposed) { return Task.CompletedTask; }

            var envelope = ExternalCorrectionEnvelope.FromJson(message.Content as string);
            if (envelope == null)
            {
                Logger.Warning("[MLAstro][Broker] Ignored a malformed message from the polar alignment plugin.");
                return Task.CompletedTask;
            }

            RaiseTraffic(BrokerLogDirection.Rx, BrokerTrafficText.Received(envelope));

            if (string.Equals(envelope.Kind, TppaBrokerKind.Capabilities, StringComparison.Ordinal))
            {
                HandleCapabilities(envelope);
            }

            try
            {
                EnvelopeReceived?.Invoke(this, envelope);
            }
            catch (Exception ex)
            {
                Logger.Error($"[MLAstro][Broker] Message handler failed: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        /// <summary>Raises <see cref="Traffic"/> without letting a UI handler break the protocol.</summary>
        private void RaiseTraffic(BrokerLogDirection direction, string detail)
        {
            try { Traffic?.Invoke(this, new BrokerTrafficEventArgs(direction, detail)); }
            catch (Exception ex) { Logger.Error($"[MLAstro][Broker] Traffic log handler failed: {ex.Message}"); }
        }

        private void HandleCapabilities(ExternalCorrectionEnvelope envelope)
        {
            var capabilities = envelope.PayloadAs<TppaCapabilities>();
            if (capabilities == null) { return; }

            lock (_gate)
            {
                _capabilities = capabilities;
                _lastReplyAt = DateTimeOffset.UtcNow;
            }

            SetStatus(BuildStatusText(capabilities));
        }

        internal static string BuildStatusText(TppaCapabilities capabilities)
        {
            if (capabilities == null) { return "TPPA: not found"; }
            if (capabilities.InterfaceVersion != TppaBrokerContract.InterfaceVersion)
            {
                return $"TPPA: incompatible (v{capabilities.InterfaceVersion})";
            }

            var version = string.IsNullOrWhiteSpace(capabilities.TppaVersion) ? "?" : capabilities.TppaVersion;
            if (capabilities.SessionActive)
            {
                return $"TPPA: v{version} running a session";
            }
            return $"TPPA: v{version} ready";
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

            if (changed)
            {
                try { StatusChanged?.Invoke(this, EventArgs.Empty); } catch (Exception ex) { Logger.Error(ex); }
            }
        }

        private void OnAnnounceTick(object sender, ElapsedEventArgs e)
        {
            if (_disposed || !Enabled) { return; }
            _ = AnnounceAsync().ContinueWith(t => Logger.Error(t.Exception),
                                             TaskContinuationOptions.OnlyOnFaulted);
        }

        public void Dispose()
        {
            if (_disposed) { return; }
            _disposed = true;
            Stop();
            _announceTimer.Dispose();
        }
    }
}
