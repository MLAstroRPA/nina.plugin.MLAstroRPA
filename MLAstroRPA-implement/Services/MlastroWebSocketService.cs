using MLAstroRPA.Dockables;
using MLAstroRPA.Settings;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MLAstroRPA.Services
{
    /// <summary>
    /// WIRELESS transport for MLAstroRPA: it connects to the device over a WebSocket
    /// (default ws://MLAstroRPA.local/ws, or a direct IP when mDNS does not resolve).
    ///
    /// Highlights:
    ///  - It uses the SAME /ws endpoint as the Web UI and identifies itself with the "MLAstroRPA-TC" handshake
    ///    keyword (like serial) -> the firmware grants the PC control + monitor and locks the web controls.
    ///  - JSON telemetry is turned back into the EXACT text format of the serial firmware and pushed
    ///    into <see cref="SerialConnectionService.InjectIncomingText"/> -> every existing parser/UI
    ///    (CONTROL + HARDWARE SETTING + the TPPA dock) behaves exactly like it does over the COM port.
    ///  - Text commands of the serial firmware are translated into the WebSocket API JSON.
    /// </summary>
    public sealed class MlastroWebSocketService : INotifyPropertyChanged, IDisposable
    {
        public const string HandshakeKey = "MLAstroRPA-TC";

        private const int ReceiveChunkSize = 16 * 1024;
        private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan ConfigAckTimeout = TimeSpan.FromSeconds(8);

        // RETRY policy on connect: only a few attempts within a SHORT window (5 s by default), then
        // report failure. Endless retries on a wrong address or an unresolvable mDNS name freeze the UI and the user
        // never learns that the connection failed.
        private static readonly TimeSpan ConnectAttemptWindow = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan ConnectRetryDelay = TimeSpan.FromMilliseconds(700);
        // Per-attempt timeout: mDNS/DNS can hang for a long time on a wrong hostname, and a TCP connect to an
        // IP on the wrong subnet can hang for ~20 s.
        private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan ConnectAttemptTimeout = TimeSpan.FromSeconds(2);

        private readonly PluginSettings _settings;
        private readonly SerialConnectionService _serial;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly object _stateLock = new();

        private ClientWebSocket? _socket;
        private CancellationTokenSource? _cts;
        private Task? _receiveLoop;
        private TaskCompletionSource<bool>? _handshakeTcs;
        private TaskCompletionSource<string>? _configAckTcs;
        private readonly Dictionary<string, Dictionary<string, object>> _snapshotSections = new(StringComparer.OrdinalIgnoreCase);
        // Configuration values live at the TOP LEVEL of the frame (they belong to no section) - e.g. the STA
        // info (ssid/ip/sta_mac), because the firmware keeps the old names for the web UI to read.
        private readonly Dictionary<string, object> _snapshotScalars = new(StringComparer.OrdinalIgnoreCase);
        // The latest align error (d/m/s + direction) the plugin KNOWS: used when the align command carries only the trigger flag
        // (AzAN/AlAN/AAll) or when a write command sends only part of the fields - like Serial (the firmware uses/keeps
        // the value stored in FRAM).
        private (int D, int M, double S, bool Dir)? _alignAzParts;
        private (int D, int M, double S, bool Dir)? _alignAltParts;
        private DateTime _lastAlignSentUtc = DateTime.MinValue;
        private bool _alignInFlight;
        private bool _sawBusySinceAlign;
        private int _speedLevelFromSnapshot = 3;
        // Firmware version logged last: a `config_pushed` frame also carries `fw_ver`, so without the
        // comparison every settings change would add another "Firmware: ..." line to the System log.
        private string? _firmwareVersionFromSnapshot;

        // The dock "mode" state (JoRe/ReDe/ReAM/ReAS exist only in the serial protocol):
        // the WebSocket has no equivalent command, so it has to be remembered to translate an arrow press into
        // `move` (continuous jog) or `moveRelative` (a single step).
        private bool _relativeMode;
        private int _relativeDegrees;
        private int _relativeMinutes;
        private int _relativeSeconds;

        // A jog is running: the dock re-sends the command every 250 ms (watchdog) but the WS firmware refuses
        // every motion command while one runs -> the repeated sends have to be dropped to avoid alert spam.
        private string? _activeJogAxis;
        private int _activeJogDirection;

        /// <summary>Singleton so the controller and the TPPA driver SHARE one WS session (the firmware allows one PC).</summary>
        public static MlastroWebSocketService? Instance { get; private set; }

        /// <summary>Every assembled text line (telemetry / ok / AAll:COMPLETED) for the TPPA ISerialLink adapter.</summary>
        public event Action<string>? LineReceived;

        /// <summary>The connection state changed (arg = IsConnected).</summary>
        public event Action<bool>? StateChanged;

        /// <summary>STOP/E-STOP pressed on MLAstro (or the link dropped) -> TPPA has to stop the PA.</summary>
        public event Action<string>? StopRequested;

        public event PropertyChangedEventHandler? PropertyChanged;

        public MlastroWebSocketService(PluginSettings settings, SerialConnectionService serial)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _serial = serial ?? throw new ArgumentNullException(nameof(serial));

            // Becomes the facade for the UI/dock: from here on SerialConnectionService reports state and sends commands
            // through this very WebSocket session when the user picks the Wireless connection.
            _serial.WirelessProxy = this;

            Instance = this;
        }

        // ==================================================================
        // State
        // ==================================================================
        private bool _isConnected;
        public bool IsConnected
        {
            get => _isConnected;
            private set
            {
                if (_isConnected == value) return;
                _isConnected = value;
                OnPropertyChanged();
                try { StateChanged?.Invoke(value); } catch { }
            }
        }

        private string _connectionStatus = "Disconnected";
        public string ConnectionStatus
        {
            get => _connectionStatus;
            private set
            {
                if (_connectionStatus == value) return;
                _connectionStatus = value;
                OnPropertyChanged();
            }
        }

        private string _handshakeStatus = string.Empty;
        public string HandshakeStatus
        {
            get => _handshakeStatus;
            private set
            {
                if (_handshakeStatus == value) return;
                _handshakeStatus = value;
                OnPropertyChanged();
            }
        }

        // ==================================================================
        // ENTRY ROUTE + STA QUALITY (reported by the firmware)
        //   LinkPath   : "AP" = the PC joined the device hotspot; "STA" = through the router.
        //                The firmware derives it from the remoteIP of EACH client in the handshakeResult frame.
        //   StaQuality : 0 = not joined to the router, 1 = router but no internet, 2 = internet
        //                (`sta_qual` field, probed with TCP inside the firmware).
        //   StaIp      : the LAN IP the router gave the ESP32 (`sta_ip` field).
        // ==================================================================
        private string _linkPath = string.Empty;
        public string LinkPath
        {
            // Read through IsConnected: a dropped link empties it by itself, so no reset is needed on every exit path.
            get => IsConnected ? _linkPath : string.Empty;
            private set
            {
                if (_linkPath == value) return;
                _linkPath = value;
                OnPropertyChanged();
            }
        }

        private int _staQuality;
        public int StaQuality
        {
            get => _staQuality;
            private set
            {
                if (_staQuality == value) return;
                _staQuality = value;
                OnPropertyChanged();
            }
        }

        // Device AP (the hotspot the ESP32 publishes): whether it is up, and its current IP.
        // Used by the "AP: Connected/Ready/Error <IP>" line in the HeaderBar.
        private bool _apReady;
        public bool ApReady
        {
            get => _apReady;
            private set
            {
                if (_apReady == value) return;
                _apReady = value;
                OnPropertyChanged();
            }
        }

        // Latest AP IP reported by the firmware (`ap_ip` field); empty = none yet.
        private string _apIp = string.Empty;

        public string ConfiguredAddress => string.IsNullOrWhiteSpace(_settings.MlaHost) ? "MLAstroRPA.local" : _settings.MlaHost;

        private bool _externalControlActive;
        public bool IsExternalControlActive
        {
            get { lock (_stateLock) return _externalControlActive; }
        }

        private readonly List<Action<bool>> _externalControlListeners = new();
        private readonly List<Action<string>> _externalStopListeners = new();

        public void AddExternalControlListener(Action<bool> listener)
        {
            if (listener == null) return;
            lock (_stateLock) { if (!_externalControlListeners.Contains(listener)) _externalControlListeners.Add(listener); }
        }

        public void RemoveExternalControlListener(Action<bool> listener)
        {
            if (listener == null) return;
            lock (_stateLock) { _externalControlListeners.Remove(listener); }
        }

        public void AddExternalStopListener(Action<string> listener)
        {
            if (listener == null) return;
            lock (_stateLock) { if (!_externalStopListeners.Contains(listener)) _externalStopListeners.Add(listener); }
        }

        public void RemoveExternalStopListener(Action<string> listener)
        {
            if (listener == null) return;
            lock (_stateLock) { _externalStopListeners.Remove(listener); }
        }

        public void NotifyExternalStop(string reason)
        {
            Logger.Info($"[MLAstro][WS] NotifyExternalStop: {reason}");
            List<Action<string>>? copy;
            lock (_stateLock) { copy = _externalStopListeners.Count > 0 ? _externalStopListeners.ToList() : null; }
            if (copy != null)
            {
                foreach (var l in copy)
                {
                    try { l(reason); } catch { }
                }
            }

            // ALWAYS raise the StopRequested event - this is the channel of the TPPA-wireless transport
            // (MlastroWirelessSerial subscribes to this event and NOT to the external-stop listener).
            // This method used to return early when no external-stop listener existed => STOP/E-STOP pressed on the
            // MLAstro plugin while TPPA ran Wireless never reached TPPA (no toast, no
            // routine stop). User-reported bug 2026-09-16 (the log only had the NotifyExternalStop line).
            try { StopRequested?.Invoke(reason); } catch { }
        }

        public void SetExternalPauseQuery(bool pause)
        {
            // Wireless: the firmware pushes telemetry periodically and there is no "?" poll -> nothing to pause.
            Logger.Info($"[MLAstro][WS] SetExternalPauseQuery({pause}) ignored (fw push telemetry).");
        }

        // ==================================================================
        // Connect / disconnect
        // ==================================================================
        public async Task<bool> ConnectAsync(CancellationToken token = default)
        {
            if (IsConnected) return true;

            // New session: clear the table of the previous session (like refreshing the Web UI page) so it only
            // shows the events of this connection. The firmware does not replay old logs to the PC either.
            ClearSystemLog();

            var host = ConfiguredAddress.Trim();
            var port = _settings.MlaPort <= 0 ? 80 : _settings.MlaPort;
            var path = string.IsNullOrWhiteSpace(_settings.MlaPath) ? "/ws" : _settings.MlaPath;
            if (!path.StartsWith("/")) path = "/" + path;

            ConnectionStatus = $"Resolving {host}...";
            HandshakeStatus = string.Empty;
            AppendLog($"Resolving {host} ...");

            // ---- Resolve + connect retries within ConnectAttemptWindow, then REPORT FAILED ----
            var deadline = DateTime.UtcNow + ConnectAttemptWindow;
            var isIpLiteral = IPAddress.TryParse(host, out _);
            var attempt = 0;
            string? lastError = null;
            ClientWebSocket? socket = null;
            Uri? uri = null;

            while (socket == null)
            {
                attempt++;

                // Once the retry window is over no further attempt is made (otherwise every attempt could wait
                // another ConnectAttemptTimeout -> the total would run far past 5 s).
                if (attempt > 1 && DateTime.UtcNow >= deadline) break;

                string? endpointHost = null;

                if (isIpLiteral)
                {
                    endpointHost = host;
                }
                else
                {
                    ConnectionStatus = $"Resolving {host}... (attempt {attempt})";
                    var resolved = await ResolveMdnsAsync(host, token).ConfigureAwait(false);
                    if (resolved == null)
                    {
                        lastError = $"Cannot resolve {host} (mDNS/DNS failed).";
                        AppendLog($"ERROR: {lastError} Enter the device IP instead.");
                    }
                    else
                    {
                        endpointHost = resolved.ToString();
                        AppendLog($"mDNS {host} -> {endpointHost}");
                    }
                }

                if (endpointHost != null)
                {
                    var candidateUri = new Uri($"ws://{endpointHost}:{port}{path}");
                    var candidate = new ClientWebSocket();
                    candidate.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);

                    try
                    {
                        ConnectionStatus = $"Connecting to {candidateUri.Host}:{port}... (attempt {attempt})";
                        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                        connectCts.CancelAfter(ConnectAttemptTimeout);
                        await candidate.ConnectAsync(candidateUri, connectCts.Token).ConfigureAwait(false);
                        socket = candidate;
                        uri = candidateUri;
                    }
                    catch (Exception ex)
                    {
                        candidate.Dispose();
                        lastError = ex.Message;
                        AppendLog($"ERROR: {ex.Message}");
                        Logger.Warning($"[MLAstro][WS] Connect attempt {attempt} failed: {ex.Message}");
                    }
                }

                if (socket != null) break;
                if (DateTime.UtcNow >= deadline) break;
                await Task.Delay(ConnectRetryDelay, token).ConfigureAwait(false);
            }

            if (socket == null)
            {
                ConnectionStatus = $"Cannot connect to {host} (wireless failed after {attempt} attempt(s) / {ConnectAttemptWindow.TotalSeconds:0}s). {lastError}";
                AppendLog($"ERROR: wireless connect failed - {lastError}");
                Logger.Error($"[MLAstro][WS] Connect failed after {attempt} attempt(s) in {ConnectAttemptWindow.TotalSeconds:0}s: {lastError}");
                return false;
            }

            _socket = socket;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            _snapshotSections.Clear();

            // Read the init snapshot + wait for the handshake
            _handshakeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _receiveLoop = Task.Run(() => ReceiveLoopAsync(socket, _cts.Token));

            var connectedHost = uri?.Host ?? host;
            AppendLog($"Connected to {connectedHost}:{port}{path}. Sending handshake '{HandshakeKey}'...");
            await SendJsonAsync(BuildHandshake(), _cts.Token).ConfigureAwait(false);

            var completed = await Task.WhenAny(_handshakeTcs.Task, Task.Delay(HandshakeTimeout, _cts.Token)).ConfigureAwait(false);
            if (completed != _handshakeTcs.Task || !_handshakeTcs.Task.Result)
            {
                var reason = HandshakeStatus;
                ConnectionStatus = string.IsNullOrWhiteSpace(reason)
                    ? "Handshake failed (no answer from device)."
                    : $"Handshake refused: {reason}";
                AppendLog($"ERROR: handshake failed - {reason}");
                AbortSocket();
                return false;
            }

            IsConnected = true;
            HandshakeStatus = "OK!";
            ConnectionStatus = $"Connected (wireless) - {connectedHost}";

            // New session: clear leftover error state (wireless never receives ERROR: lines like serial does).
            try { _serial.ResetErrorStateForNewSession(); } catch { }

            AppendLog($"Handshake: OK! PC has control; Web UI locked (monitoring only).");
            Logger.Info($"[MLAstro][WS] Connected and handshaked on {connectedHost}:{port}{path}");
            return true;
        }

        private static async Task<IPAddress?> ResolveMdnsAsync(string host, CancellationToken token)
        {
            try
            {
                // DNS/mDNS can hang for a very long time on a wrong hostname -> cap it with ResolveTimeout and
                // return null so the ConnectAsync retry loop decides (instead of hanging forever).
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(ResolveTimeout);
                var addresses = await Dns.GetHostAddressesAsync(host, cts.Token).ConfigureAwait(false);
                return addresses?.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                       ?? addresses?.FirstOrDefault();
            }
            catch (Exception ex)
            {
                Logger.Warning($"[MLAstro][WS] Resolve '{host}' failed: {ex.Message}");
                return null;
            }
        }

        private string BuildHandshake()
        {
            var payload = new
            {
                cmd = "handshake",
                data = new
                {
                    key = HandshakeKey,
                    client = "NINA-MLAstroRPA+TPPA"
                }
            };
            return JsonSerializer.Serialize(payload);
        }

        public void Disconnect()
        {
            try
            {
                if (IsConnected)
                {
                    // Releases control cleanly so the Web UI unlocks at once (no F5 needed)
                    try { SendJsonAsync("{\"cmd\":\"releaseControl\"}", CancellationToken.None).GetAwaiter().GetResult(); } catch { }
                    AppendLog("Release control sent. Disconnecting...");
                }
            }
            catch { }

            AbortSocket();
            IsConnected = false;
            HandshakeStatus = string.Empty;
            ConnectionStatus = "Disconnected";
            AppendLog("Disconnected (wireless).");
        }

        private void AbortSocket()
        {
            _activeJogAxis = null;
            _activeJogDirection = 0;
            _alignInFlight = false;
            _sawBusySinceAlign = false;

            try { _cts?.Cancel(); } catch { }
            try { _socket?.Abort(); } catch { }
            try { _socket?.Dispose(); } catch { }
            _socket = null;
            try { _cts?.Dispose(); } catch { }
            _cts = null;
            _receiveLoop = null;
            _handshakeTcs = null;
            _configAckTcs = null;
            lock (_stateLock)
            {
                if (_externalControlActive)
                {
                    _externalControlActive = false;
                    foreach (var l in _externalControlListeners.ToList()) { try { l(false); } catch { } }
                }
            }
        }

        public async Task<bool> EnsureExternalConnectedAsync()
        {
            if (IsConnected) return true;
            return await ConnectAsync().ConfigureAwait(false);
        }

        public async Task<bool> BeginExternalControlAsync()
        {
            if (!await EnsureExternalConnectedAsync().ConfigureAwait(false)) return false;
            lock (_stateLock)
            {
                _externalControlActive = true;
                foreach (var l in _externalControlListeners.ToList()) { try { l(true); } catch { } }
            }
            return true;
        }

        public void EndExternalControl()
        {
            lock (_stateLock)
            {
                if (!_externalControlActive) return;
                _externalControlActive = false;
                foreach (var l in _externalControlListeners.ToList()) { try { l(false); } catch { } }
            }
        }

        /// <summary>
        /// Reboots the device over the WebSocket - EXACTLY like the REBOOT button of the Web UI: it sends
        /// <c>{"cmd":"reboot"}</c> so the firmware broadcasts <c>sys_status=REBOOTING</c> and then runs <c>ESP.restart()</c>.
        /// WARNING: do NOT send <c>releaseControl</c> first - once control is released the client is no longer the
        /// PC-controller, so the firmware refuses the reboot (it answers "locked") => the Reset ESP32 button over
        /// Wireless did nothing silently. The reboot clears the PC session on the device itself, so releasing is not needed.
        /// </summary>
        public bool ResetEsp32()
        {
            if (!IsConnected) return false;
            try
            {
                SendJsonAsync("{\"cmd\":\"reboot\"}", CancellationToken.None).GetAwaiter().GetResult();
                AppendLog("Reboot command sent (wireless).");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warning($"[MLAstro][WS] reboot command failed: {ex.Message}");
                return false;
            }
        }

        public bool QueryTelemetry() => IsConnected; // the firmware pushes telemetry about every 250 ms

        // ==================================================================
        // Incoming data
        // ==================================================================
        private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken token)
        {
            var buffer = new byte[ReceiveChunkSize];
            var message = new StringBuilder();

            try
            {
                while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    message.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    if (!result.EndOfMessage) continue;

                    var text = message.ToString();
                    message.Clear();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        HandleIncoming(text);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Logger.Warning($"[MLAstro][WS] Receive loop ended: {ex.Message}");
            }
            finally
            {
                var wasConnected = IsConnected;
                IsConnected = false;
                HandshakeStatus = string.Empty;
                ConnectionStatus = "Disconnected";
                if (wasConnected)
                {
                    AppendLog("Connection lost (wireless).");
                    // The device can no longer be driven -> TPPA has to stop the running PA.
                    try { StopRequested?.Invoke("Wireless connection closed."); } catch { }
                }
            }
        }

        private void HandleIncoming(string json)
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(json); }
            catch (Exception ex)
            {
                Logger.Warning($"[MLAstro][WS] JSON parse failed: {ex.Message}");
                return;
            }

            using (doc)
            {
                var root = doc.RootElement;

                // Log/alert pushed by the firmware
                if (TryGetString(root, "log", out var logMsg))
                {
                    AddSystemLog(logMsg); // the main content of the System log table (like the Web UI)
                }
                if (TryGetString(root, "alert", out var alert))
                {
                    // The Web UI shows `alert` as a MODAL and does NOT write it to the System log (see
                    // data/script.js: showModal('System Message', data.alert) - it never calls appendLog).
                    // The plugin keeps the same behaviour: the NINA log file (and a toast, see below), but NOT the
                    // System log -> so the plugin table always matches the Web UI System Log table.
                    AppendLog($"ALERT: {alert}");

                    // LIMIT-RELATED warnings (soft/hard limit, refused align/relative) do NOT
                    // raise a toast any more: the firmware already reports the SAME event as an error code in the ERROR telemetry
                    // (AzSL/AlSL/AzHL/AlHL/RfJog*/RfAln*) -> jogging into a soft limit used to show two dialogs
                    // ("Soft Limit Reached! AZ axis stopped at configured limit." from `alert` and
                    // "AZ soft limit reached" from the AzSL error code).
                    // The error-code channel wins because it exists in EVERY control environment (Serial
                    // has no `alert`) -> the wording and the number of notices match whether a cable or WS is used.
                    // The more detailed `alert` text (e.g. which direction escapes a hard limit) is still
                    // written to the NINA log file through AppendLog above.
                    if (!IsLimitAlert(alert))
                    {
                        try { Notification.ShowWarning($"MLAstro RPA: {alert}"); } catch { }
                    }
                }

                // 1) Handshake result
                if (TryGetString(root, "cmd", out var cmd))
                {
                    if (cmd == "handshakeResult")
                    {
                        var ok = TryGetBool(root, "result", out var r) && r;
                        if (!ok && TryGetString(root, "reason", out var reason))
                        {
                            HandshakeStatus = reason;
                        }
                        if (ok && TryGetString(root, "fw_ver", out var fwVer))
                        {
                            _serial.SetWirelessFirmwareVersion(fwVer);
                        }
                        // Entry route (AP/STA) - the firmware derives it from the remoteIP of this very client.
                        if (ok && TryGetString(root, "link", out var linkPath))
                        {
                            LinkPath = linkPath;
                        }
                        _handshakeTcs?.TrySetResult(ok);
                        return;
                    }

                    if (cmd == "connectionRejected")
                    {
                        var reason = TryGetString(root, "reason", out var r2) ? r2 : "Connection rejected by device.";
                        HandshakeStatus = reason;
                        ConnectionStatus = reason;
                        AppendLog($"ERROR: {reason}");
                        _handshakeTcs?.TrySetResult(false);
                        return;
                    }

                    if (cmd == "controlReleased" || cmd == "controlTakenBySerial")
                    {
                        // The Web UI does exactly that: appendLog(data.reason) - no prefix is added.
                        var reason = TryGetString(root, "reason", out var r3) ? r3 : cmd;
                        AddSystemLog(reason);
                        return;
                    }

                    if (cmd == "releaseControlResult")
                    {
                        return;
                    }

                    if (cmd == "configRead")
                    {
                        // The `getConfig` answer (it reads the WiFi password on demand). It is pushed into the
                        // firmware text stream as if the device had answered `STAp:...` / `APpa:...` -> the
                        // serial path updates settings + UI by itself (exactly like a USB cable connection).
                        if (root.TryGetProperty("data", out var cd) && cd.ValueKind == JsonValueKind.Object)
                        {
                            if (TryGetString(cd, "pass", out var staPw))
                            {
                                _serial.InjectIncomingText($"STAp:{staPw}\n");
                            }
                            if (cd.TryGetProperty("wifi_ap", out var apObj) && apObj.ValueKind == JsonValueKind.Object
                                && TryGetString(apObj, "pass", out var apPw))
                            {
                                _serial.InjectIncomingText($"APpa:{apPw}\n");
                            }
                        }
                        return;
                    }
                }

                // 2) Config ack
                if (TryGetString(root, "status", out var status))
                {
                    if (status == "configSaved" || status == "configApplied")
                    {
                        _configAckTcs?.TrySetResult(status);
                        return;
                    }
                }

                // 3) Configuration snapshot: the first frame after connecting AND every `config_pushed` frame
                //    (the firmware pushes again when the configuration changes from any route) -> always cached.
                //    NOTE: this branch ALSO catches frames carrying a SINGLE setting (e.g. `{"speedLevel":2}` when the
                //    speed changes, or `handshakeResult` with `fw_ver`) because the firmware sends per-setting deltas.
                //    Every helper here therefore follows the rule "only write when the field is PRESENT":
                //    a missing field does NOT mean the device reported it empty.
                //    (The WiFi password is NOT part of the snapshot - it is read with the `getConfig` command.)
                if (TryGetInt(root, "speedLevel", out _) || TryGetString(root, "fw_ver", out _))
                {
                    CacheSnapshotSections(root);
                    ApplyRelativeState(root); // the Jog/Relative state stored on the device is the source of truth
                    if (TryGetString(root, "fw_ver", out var fw) && fw != _firmwareVersionFromSnapshot)
                    {
                        _firmwareVersionFromSnapshot = fw;
                        _serial.SetWirelessFirmwareVersion(fw);
                        AppendLog($"Firmware: {fw}");
                    }
                    if (TryGetBool(root, "serial_locked", out var locked) && locked && !IsConnected)
                    {
                        AppendLog("NOTE: device reports serial control active.");
                    }
                    return;
                }

                // 3b) The firmware broadcasts the Relative state ({"relative":{...}}) - it is emitted when
                //     ANY client (web or PC plugin) changes the Relative mode/parameters.
                //     Recorded so telemetry (JoRe/ReDe/ReAM/ReAS) + the dock always match the backend.
                if (ApplyRelativeState(root))
                {
                    return;
                }

                // 4) Error-code state sent by the firmware over the WebSocket (edge-triggered, like the
                //    Serial "ERROR:..." lines). Without this branch the Alarm History table stays empty
                //    because the serial route is not used while connected over Wireless.
                if (TryGetString(root, "error", out var errorLine) && !string.IsNullOrWhiteSpace(errorLine))
                {
                    var line = errorLine.StartsWith("ERROR:", StringComparison.Ordinal) ? errorLine : "ERROR:" + errorLine;
                    // It only feeds the Alarm History table + the NINA log file. The Web UI does not write error lines to the
                    // System log either (modal only), so no extra summary line is added here to keep both log tables identical.
                    AppendLog(line);
                    _serial.InjectIncomingText(line);   // -> ProcessErrorTelemetry -> Alarm History
                    return;
                }

                // 5) Periodic telemetry: NOT written to the System log (every 250 ms would flood it),
                //    it is only turned into telemetry text for the UI pipeline + the TPPA driver.
                //    Something counts as telemetry only when a position field is present (frames with just sys_status such as
                //    STOPPED/REBOOTING must not overwrite the displayed position).
                if (root.TryGetProperty("pos_az", out _))
                {
                    var line = BuildSerialTelemetryLine(root);
                    if (!string.IsNullOrEmpty(line))
                    {
                        // For the TPPA ISerialLink adapter (ReadLine/ReadExisting)
                        try { LineReceived?.Invoke(line); } catch { }

                        // For the UI pipeline shared with serial (TelemetryParser + dock)
                        _serial.InjectIncomingText(line + "\n");

                        // Forwards the align-completed event to the controller UI + the TPPA driver.
                        // It counts as done only when: the firmware reports ALIGN_COMPLETED, OR READY after a
                        // running state was seen first (this avoids a momentary READY right after the command),
                        // OR READY after 1.5 s (very small steps have no ALIGNING phase).
                        if (TryGetString(root, "sys_status", out var sysStatus))
                        {
                            var busy = sysStatus == "ALIGNING" || sysStatus == "MOVING" || sysStatus == "HOMING" || sysStatus == "CALIBRATING";
                            if (_alignInFlight && busy)
                            {
                                _sawBusySinceAlign = true;
                            }

                            var completed = sysStatus == "ALIGN_COMPLETED"
                                || (sysStatus == "READY" && _alignInFlight
                                    && (_sawBusySinceAlign || (DateTime.UtcNow - _lastAlignSentUtc) > TimeSpan.FromSeconds(1.5)));

                            if (completed)
                            {
                                _alignInFlight = false;
                                _sawBusySinceAlign = false;
                                _serial.InjectIncomingText("AAll:COMPLETED\n");
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Configuration sections of the snapshot/broadcast frames are cached to BUILD TELEMETRY and to look up
        /// values when translating commands. MUST match fillConfigSections() in the firmware; a new firmware setting
        /// means adding its section name here (+ the matching token in AppendSnapshotTokens).
        /// </summary>
        private static readonly string[] SnapshotSectionNames =
            { "limits", "motor", "backlash", "wifi_ap", "serial", "align", "align_mode" };

        /// <summary>Top-level values of the snapshot frame needed for the telemetry text (STA info).</summary>
        private static readonly string[] SnapshotScalarNames = { "ssid", "ip", "sta_mac", "pass" };

        private void CacheSnapshotSections(JsonElement root)
        {
            if (TryGetInt(root, "speedLevel", out var speedLevel))
            {
                _speedLevelFromSnapshot = speedLevel;
            }

            foreach (var name in SnapshotSectionNames)
            {
                if (!root.TryGetProperty(name, out var section) || section.ValueKind != JsonValueKind.Object) continue;
                var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                FlattenJson(section, string.Empty, dict);
                _snapshotSections[name] = dict;
            }

            foreach (var name in SnapshotScalarNames)
            {
                if (!root.TryGetProperty(name, out var scalar)) continue;
                _snapshotScalars[name] = ToPlainValue(scalar);
            }

            // The align error STORED on the device is the source of truth (used when an align command carries no value).
            if (_snapshotSections.TryGetValue("align", out var alignSnap))
            {
                _alignAzParts = AlignPartsFromSection(alignSnap, "az");
                _alignAltParts = AlignPartsFromSection(alignSnap, "alt");
            }
        }

        /// <summary>Flattens a JSON object into "key" or "parent.child" for simple lookups.</summary>
        private static void FlattenJson(JsonElement obj, string prefix, Dictionary<string, object> into)
        {
            foreach (var prop in obj.EnumerateObject())
            {
                var key = prefix.Length == 0 ? prop.Name : prefix + "." + prop.Name;
                if (prop.Value.ValueKind == JsonValueKind.Object)
                {
                    FlattenJson(prop.Value, key, into);
                }
                else if (prop.Value.ValueKind != JsonValueKind.Array)
                {
                    into[key] = ToPlainValue(prop.Value);
                }
            }
        }

        private static object ToPlainValue(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.TryGetInt64(out var l) ? l : value.GetDouble(),
            JsonValueKind.String => value.GetString() ?? string.Empty,
            _ => string.Empty
        };

        /// <summary>The align error (d/m/s + direction) from the cached "align" section: {az:{d,m,s,dir}}.</summary>
        private static (int D, int M, double S, bool Dir)? AlignPartsFromSection(Dictionary<string, object> align, string axis)
        {
            if (!align.ContainsKey(axis + ".d") && !align.ContainsKey(axis + ".s")) return null;
            var d = ParseDecimal(Get(align, axis + ".d"));
            var m = ParseDecimal(Get(align, axis + ".m"));
            var s = ParseDecimal(Get(align, axis + ".s"));
            // GetFlag() MUST be used: `dir` is a bool in JSON and Convert.ToString(false) = "False", so
            // a string comparison like `Get(...) != "0"` reads a false flag as TRUE -> the align direction would always be
            // "Right/Up" and the user could not reverse it with the toggle on the PC client.
            var dir = GetFlag(align, axis + ".dir");
            return ((int)d, (int)m, s, dir);
        }

        /// <summary>
        /// Reads a flag from the cached dict (JSON bool / number 0-1 / string "1"|"true") as a bool.
        /// Used for EVERY flag-like field - never compare strings with "0" because bool->string is "True"/"False".
        /// </summary>
        private static bool GetFlag(Dictionary<string, object> d, string key)
        {
            if (!d.TryGetValue(key, out var v) || v == null) return false;
            return v switch
            {
                bool b => b,
                long l => l != 0,
                double db => Math.Abs(db) > double.Epsilon,
                string s => s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase),
                _ => false
            };
        }

        private static double ParseDecimal(string value)
            => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

        private string GetScalar(string name)
            => _snapshotScalars.TryGetValue(name, out var v)
                ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? string.Empty
                : string.Empty;

        /// <summary>Assembles JSON telemetry into the exact text format of the serial firmware.</summary>
        private string BuildSerialTelemetryLine(JsonElement root)
        {
            var status = TryGetString(root, "sys_status", out var s) ? s : "READY";
            var movedAz = TryGetDouble(root, "align_moved_az", out var az) ? az : 0;
            var movedAlt = TryGetDouble(root, "align_moved_alt", out var alt) ? alt : 0;

            var tokens = new List<string>();
            // Scal:1 = the scale factor of the full telemetry frame (same as the serial firmware).
            AddToken(tokens, "Scal", "1");
            AddToken(tokens, "SLvl", _speedLevelFromSnapshot.ToString(CultureInfo.InvariantCulture));
            AddToken(tokens, "WSta", TryGetDouble(root, "rssi", out var rssi) && rssi > -1000 ? "1" : "0");

            // STA quality + the LAN IP of the device (firmware 1.7.0+). They are forwarded as the
            // WQu / STAi tokens so the dock parses them through the SAME path as a Serial cable.
            // Older firmware (< 1.7.0) sends no `sta_qual` -> the minimum level is derived from RSSI (joined to the
            // router => 1) so the STA line does not wrongly show "none".
            StaQuality = TryGetInt(root, "sta_qual", out var staQual)
                ? staQual
                : (rssi > -1000 ? 1 : 0);
            if (TryGetString(root, "sta_ip", out var staIp) && !string.IsNullOrWhiteSpace(staIp))
            {
                // Written into the snapshot "ip" scalar => AppendSnapshotTokens emits the STAi token with the NEWEST IP
                // (the snapshot IP can be stale when DHCP hands out a different address while the config stays the same).
                _snapshotScalars["ip"] = staIp;
            }
            AddToken(tokens, "WQu", StaQuality.ToString(CultureInfo.InvariantCulture));

            // Device AP: up/has an IP (APrd token) + the AP IP (APip token, see
            // AppendSnapshotTokens - a live IP wins over the value in the configuration snapshot).
            if (TryGetBool(root, "ap_ready", out var apReady))
            {
                ApReady = apReady;
            }
            if (TryGetString(root, "ap_ip", out var apIp) && !string.IsNullOrWhiteSpace(apIp))
            {
                _apIp = apIp;
            }
            AddToken(tokens, "APrd", ApReady ? "1" : "0");
            AddToken(tokens, "Home", TryGetBool(root, "homed", out var homed) && homed ? "1" : "0");

            if (TryGetDouble(root, "pos_az", out var posAz))
                AddToken(tokens, "AzPH", posAz.ToString("0.#####", CultureInfo.InvariantCulture));
            if (TryGetDouble(root, "pos_alt", out var posAlt))
                AddToken(tokens, "AlPH", posAlt.ToString("0.#####", CultureInfo.InvariantCulture));

            // Relative move mode: in the Serial protocol JoRe/ReDe/ReAM/ReAS are STATE,
            // while the WebSocket has no equivalent command, so the plugin remembers them itself. They must reach telemetry,
            // otherwise the dock gets IsRelativeMode = false after every packet (the toggle turns itself off right away).
            AddToken(tokens, "JoRe", _relativeMode ? "1" : "0");
            AddToken(tokens, "ReDe", _relativeDegrees.ToString(CultureInfo.InvariantCulture));
            AddToken(tokens, "ReAM", _relativeMinutes.ToString(CultureInfo.InvariantCulture));
            AddToken(tokens, "ReAS", _relativeSeconds.ToString(CultureInfo.InvariantCulture));

            AppendSnapshotTokens(tokens);

            return $"<{status}|Mpos:{movedAz.ToString("0.#####", CultureInfo.InvariantCulture)},{movedAlt.ToString("0.#####", CultureInfo.InvariantCulture)}|>{string.Join(",", tokens)}";
        }

        private void AppendSnapshotTokens(List<string> tokens)
        {
            // The number format here MUST match the firmware snprintf() (AzL1:%.1f, AzSD:%.5f,
            // AzED:%.0f, AzES:%.2f...) so the dock/TPPA driver gets the same data types as over a Serial cable.
            if (_snapshotSections.TryGetValue("limits", out var limits))
            {
                AddToken(tokens, "AzL1", Format(limits, "az_min", "0.#"));
                AddToken(tokens, "AzL2", Format(limits, "az_max", "0.#"));
                AddToken(tokens, "AlL1", Format(limits, "alt_min", "0.#"));
                AddToken(tokens, "AlL2", Format(limits, "alt_max", "0.#"));
            }

            if (_snapshotSections.TryGetValue("motor", out var motor))
            {
                AddToken(tokens, "AzRD", Format(motor, "az_reverse", "1"));
                AddToken(tokens, "AzIR", Get(motor, "az_run_ma"));
                AddToken(tokens, "AzIH", Get(motor, "az_hold_ma"));
                AddToken(tokens, "AzSB", Get(motor, "az_boost_pct"));
                AddToken(tokens, "AzSC", Get(motor, "az_soft_cs_pct"));
                AddToken(tokens, "AzMS", Get(motor, "az_microsteps"));
                AddToken(tokens, "AzAc", Get(motor, "az_accel"));
                AddToken(tokens, "AzDec", Get(motor, "az_decel"));
                AddToken(tokens, "AzSD", Format(motor, "az_spd", "0.#####"));
                AddToken(tokens, "AzRM", Format(motor, "az_spread_cycle", "1"));
                AddToken(tokens, "AlRD", Format(motor, "alt_reverse", "1"));
                AddToken(tokens, "AlIR", Get(motor, "alt_run_ma"));
                AddToken(tokens, "AlIH", Get(motor, "alt_hold_ma"));
                AddToken(tokens, "AlSB", Get(motor, "alt_boost_pct"));
                AddToken(tokens, "AlSC", Get(motor, "alt_soft_cs_pct"));
                AddToken(tokens, "AlMS", Get(motor, "alt_microsteps"));
                AddToken(tokens, "AlAc", Get(motor, "alt_accel"));
                AddToken(tokens, "AlDe", Get(motor, "alt_decel"));
                AddToken(tokens, "AlSD", Format(motor, "alt_spd", "0.#####"));
                AddToken(tokens, "AlRM", Format(motor, "alt_spread_cycle", "1"));
            }

            if (_snapshotSections.TryGetValue("backlash", out var backlash))
            {
                AddToken(tokens, "Back", Format(backlash, "enable", "1"));
                AddToken(tokens, "AzBl", Get(backlash, "az_steps"));
                AddToken(tokens, "AlBl", Get(backlash, "alt_steps"));
                AddToken(tokens, "Over", Format(backlash, "overshoot", "1"));
                AddToken(tokens, "OvUp", Format(backlash, "overshoot_up", "1"));
                AddToken(tokens, "OvDn", Format(backlash, "overshoot_down", "1"));
                AddToken(tokens, "OvD", Get(backlash, "overshoot_d"));
                AddToken(tokens, "OvM", Get(backlash, "overshoot_m"));
                AddToken(tokens, "OvS", Get(backlash, "overshoot_s"));
            }

            if (_snapshotSections.TryGetValue("wifi_ap", out var ap))
            {
                AddToken(tokens, "APss", Get(ap, "ssid"));
                AddToken(tokens, "APma", Get(ap, "mac"));
                // AP IP: prefer the LIVE `ap_ip` (firmware 1.7.0+); the snapshot is only the saved configuration, so it
                // can differ from reality when the AP comes up with a fallback value.
                AddToken(tokens, "APip", string.IsNullOrWhiteSpace(_apIp) ? Get(ap, "ip") : _apIp);
                AddToken(tokens, "APsu", Get(ap, "subnet"));
            }

            // The STA info sits at the TOP LEVEL of the frame (the firmware keeps the old ip/ssid names for the web UI).
            AddToken(tokens, "STAs", GetScalar("ssid"));
            AddToken(tokens, "STAm", GetScalar("sta_mac"));
            AddToken(tokens, "STAi", GetScalar("ip"));

            // The align error STORED on the device (AzED/AzEM/AzES/AzDi + Al...). Without it the error
            // input boxes on the dock/TPPA driver would go blank after every telemetry packet.
            if (_snapshotSections.TryGetValue("align", out var align))
            {
                AddToken(tokens, "AzED", Format(align, "az.d", "0"));
                AddToken(tokens, "AzEM", Format(align, "az.m", "0"));
                AddToken(tokens, "AzES", Format(align, "az.s", "0.##"));
                AddToken(tokens, "AzDi", Format(align, "az.dir", "1"));
                AddToken(tokens, "AlED", Format(align, "alt.d", "0"));
                AddToken(tokens, "AlEM", Format(align, "alt.m", "0"));
                AddToken(tokens, "AlES", Format(align, "alt.s", "0.##"));
                AddToken(tokens, "AlDi", Format(align, "alt.dir", "1"));
            }
        }

        /// <summary>
        /// Formats dict values with the firmware number patterns ("0.#" = %.1f, "0.#####" = %.5f,
        /// "1" = a 0/1 flag). Non-numeric values (ssid/ip/mac) are kept as they are.
        /// </summary>
        private static string Format(Dictionary<string, object> d, string key, string format)
        {
            if (!d.TryGetValue(key, out var v) || v == null) return string.Empty;
            if (v is bool b) return b ? "1" : "0";
            var s = Convert.ToString(v, CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var num)
                ? num.ToString(format, CultureInfo.InvariantCulture)
                : s;
        }

        private static string Get(Dictionary<string, object> d, string key)
            => d.TryGetValue(key, out var v) ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? string.Empty : string.Empty;

        private static void AddToken(List<string> tokens, string key, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            tokens.Add($"{key}:{value}");
        }

        // ==================================================================
        // Sending commands (firmware text protocol -> WebSocket API JSON)
        // ==================================================================
        public bool Send(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return false;
            if (!IsConnected)
            {
                AppendLog($"ERROR: not connected - dropped: {line.Trim()}");
                return false;
            }

            try
            {
                var tokens = ParseCommandLine(line);
                if (tokens.Count == 0) return false;

                var outgoing = Translate(tokens);
                if (outgoing.Count == 0)
                {
                    // Commands that need no send (e.g. "?" because the firmware pushes telemetry)
                    return true;
                }

                foreach (var json in outgoing)
                {
                    SendJsonAsync(json, _cts?.Token ?? CancellationToken.None).GetAwaiter().GetResult();
                }
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"[MLAstro][WS] Send failed: {ex.Message}");
                AppendLog($"ERROR: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Pushes the Relative mode/parameters DOWN to the firmware, exactly like the Web UI does
        /// (saveRelativeSettings() -> saveConfig). That keeps the backend the single source of truth:
        /// the firmware writes FRAM and broadcasts {"relative":{...}} to EVERY client (web + PC).
        /// </summary>
        private void PushRelativeSettings()
        {
            if (!IsConnected) return;

            try
            {
                var payload = new
                {
                    origin = "pcPlugin", // so a configSaved ack is not taken for the ack of a web save
                    relative = new
                    {
                        mode = _relativeMode,
                        d = _relativeDegrees,
                        m = _relativeMinutes,
                        s = _relativeSeconds
                    }
                };

                var json = JsonSerializer.Serialize(new { cmd = "saveConfig", data = payload });
                SendJsonAsync(json, _cts?.Token ?? CancellationToken.None).GetAwaiter().GetResult();
                Logger.Info($"[MLAstro][WS] Relative pushed: mode={_relativeMode}, {_relativeDegrees}d {_relativeMinutes}m {_relativeSeconds}s");
            }
            catch (Exception ex)
            {
                Logger.Warning($"[MLAstro][WS] PushRelativeSettings failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Reads the Relative state BACK from the backend (the {"relative":{...}} broadcast or the init
        /// snapshot). That way the plugin never drifts apart from the firmware/Web client.
        /// </summary>
        private bool ApplyRelativeState(JsonElement container)
        {
            try
            {
                if (container.ValueKind != JsonValueKind.Object) return false;
                if (!container.TryGetProperty("relative", out var rel) || rel.ValueKind != JsonValueKind.Object) return false;

                if (rel.TryGetProperty("mode", out var mode) &&
                    (mode.ValueKind == JsonValueKind.True || mode.ValueKind == JsonValueKind.False))
                {
                    _relativeMode = mode.GetBoolean();
                }
                if (rel.TryGetProperty("d", out var d) && d.ValueKind == JsonValueKind.Number) _relativeDegrees = d.GetInt32();
                if (rel.TryGetProperty("m", out var m) && m.ValueKind == JsonValueKind.Number) _relativeMinutes = m.GetInt32();
                if (rel.TryGetProperty("s", out var s) && s.ValueKind == JsonValueKind.Number) _relativeSeconds = s.GetInt32();
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warning($"[MLAstro][WS] ApplyRelativeState failed: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> SendCommandAndAwaitOkAsync(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (!IsConnected) return false;

            var tokens = ParseCommandLine(text);
            if (tokens.Count == 0) return false;

            var isConfig = tokens.Keys.Any(k => ConfigKeyMap.ContainsKey(k));
            var hasSaveAndReboot = tokens.Keys.Any(k => k.Equals("Save&Reboot", StringComparison.OrdinalIgnoreCase));

            if (!isConfig)
            {
                foreach (var json in Translate(tokens))
                {
                    await SendJsonAsync(json, _cts?.Token ?? CancellationToken.None).ConfigureAwait(false);
                }
                return true;
            }

            var payload = BuildConfigPayload(tokens);
            if (payload.Count == 0)
            {
                AppendLog("NOTE: no WS-supported config keys in payload.");
                return false;
            }

            var command = hasSaveAndReboot ? "saveConfig" : "applyConfig";

            // WiFi (STA) / WiFi (AP) can only reach FRAM through saveConfig, and the firmware REBOOTS by itself
            // right after the save unless no_reboot is set -> the client would never get the configSaved ack.
            // So no_reboot:true is always sent and the reboot command follows once the save is confirmed (like
            // the web UI does), which avoids a reboot before the save is certain.
            if (command == "saveConfig" && payload.Keys.Any(IsSaveOnlySection))
            {
                payload["no_reboot"] = true;
            }

            var jsonBody = JsonSerializer.Serialize(new { cmd = command, data = payload });

            _configAckTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            await SendJsonAsync(jsonBody, _cts?.Token ?? CancellationToken.None).ConfigureAwait(false);

            var ackTask = _configAckTcs.Task;
            var completed = await Task.WhenAny(ackTask, Task.Delay(ConfigAckTimeout)).ConfigureAwait(false);
            if (completed != ackTask)
            {
                AppendLog($"WARNING: no {command} confirmation within {ConfigAckTimeout.TotalSeconds:0}s.");
                return false;
            }

            AppendLog($"Device confirmed: {ackTask.Result}");

            if (hasSaveAndReboot)
            {
                // The firmware does not reboot itself when the config is saved over WS -> send reboot like the serial protocol.
                await SendJsonAsync("{\"cmd\":\"reboot\"}", CancellationToken.None).ConfigureAwait(false);
                AppendLog("Reboot command sent after save.");
            }
            return true;
        }

        private static readonly Dictionary<string, (string Section, string Field, bool IsBool)> ConfigKeyMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["AzL1"] = ("limits", "az_min", false),
            ["AzL2"] = ("limits", "az_max", false),
            ["AlL1"] = ("limits", "alt_min", false),
            ["AlL2"] = ("limits", "alt_max", false),
            ["AzRD"] = ("motor", "az_reverse", true),
            ["AzIR"] = ("motor", "az_run_ma", false),
            ["AzIH"] = ("motor", "az_hold_ma", false),
            ["AzSB"] = ("motor", "az_boost_pct", false),
            ["AzSC"] = ("motor", "az_soft_cs_pct", false),
            ["AzMS"] = ("motor", "az_microsteps", false),
            ["AzAc"] = ("motor", "az_accel", false),
            ["AzDec"] = ("motor", "az_decel", false),
            ["AzSD"] = ("motor", "az_spd", false),
            ["AzRM"] = ("motor", "az_spread_cycle", true),
            ["AlRD"] = ("motor", "alt_reverse", true),
            ["AlIR"] = ("motor", "alt_run_ma", false),
            ["AlIH"] = ("motor", "alt_hold_ma", false),
            ["AlSB"] = ("motor", "alt_boost_pct", false),
            ["AlSC"] = ("motor", "alt_soft_cs_pct", false),
            ["AlMS"] = ("motor", "alt_microsteps", false),
            ["AlAc"] = ("motor", "alt_accel", false),
            ["AlDe"] = ("motor", "alt_decel", false),
            ["AlSD"] = ("motor", "alt_spd", false),
            ["AlRM"] = ("motor", "alt_spread_cycle", true),
            ["Back"] = ("backlash", "enable", true),
            ["AzBl"] = ("backlash", "az_steps", false),
            ["AlBl"] = ("backlash", "alt_steps", false),
            ["Over"] = ("backlash", "overshoot", true),
            ["OvUp"] = ("backlash", "overshoot_up", true),
            ["OvDn"] = ("backlash", "overshoot_down", true),
            ["OvD"] = ("backlash", "overshoot_d", false),
            ["OvM"] = ("backlash", "overshoot_m", false),
            ["OvS"] = ("backlash", "overshoot_s", false),

            // WiFi (AP) & WiFi (STA): the firmware takes these two groups in saveConfig (FRAM write + reboot),
            // the equivalent of the APss/APpa/APip/APsu/STAs/STAp commands of the Serial protocol.
            // They used to be ignored -> changing WiFi/AP over Wireless had no effect.
            ["APss"] = ("wifi_ap", "ssid", false),
            ["APpa"] = ("wifi_ap", "pass", false),
            ["APip"] = ("wifi_ap", "ip", false),
            ["APsu"] = ("wifi_ap", "subnet", false),
            ["STAs"] = ("wifi", "ssid", false),
            ["STAp"] = ("wifi", "pass", false),
        };

        /// <summary>Configuration groups that only apply when SAVING (saveConfig) - WiFi/AP.</summary>
        private static bool IsSaveOnlySection(string section)
            => section.Equals("wifi", StringComparison.OrdinalIgnoreCase)
               || section.Equals("wifi_ap", StringComparison.OrdinalIgnoreCase);

        private Dictionary<string, object> BuildConfigPayload(Dictionary<string, string> tokens)
        {
            var sections = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in tokens)
            {
                if (!ConfigKeyMap.TryGetValue(kv.Key, out var map)) continue;
                if (!sections.TryGetValue(map.Section, out var section))
                {
                    section = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    sections[map.Section] = section;
                }
                section[map.Field] = map.IsBool ? (object)(kv.Value == "1") : ParseNumber(kv.Value);
            }

            return sections.ToDictionary(
                s => s.Key,
                s => (object)s.Value,
                StringComparer.OrdinalIgnoreCase);
        }

        private static object ParseNumber(string value)
        {
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
            return value;
        }

        private static Dictionary<string, string> ParseCommandLine(string line)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in line.Split(new[] { ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var token = raw.Trim();
                if (token.Length == 0) continue;
                var idx = token.IndexOf(':');
                if (idx <= 0) continue;
                result[token.Substring(0, idx).Trim()] = token.Substring(idx + 1).Trim();
            }
            return result;
        }

        private List<string> Translate(Dictionary<string, string> tokens)
        {
            var outgoing = new List<string>();
            if (tokens.Count == 0) return outgoing;

            // Telemetry query: the firmware pushes it, nothing to send
            if (tokens.Count == 1 && tokens.ContainsKey("?")) return outgoing;

            // Serial handshake text: the WS transport already handshook on connect
            if (tokens.ContainsKey("[MLAstroRPA-TC]")) return outgoing;

            if (tokens.TryGetValue("Disconnect", out _))
            {
                outgoing.Add("{\"cmd\":\"releaseControl\"}");
                return outgoing;
            }

            // `reboot` (text protocol) -> `{"cmd":"reboot"}`, exactly like the REBOOT button of the Web UI.
            // This token used to be unmapped and swallowed => the Reset ESP32 button over Wireless
            // could not reboot the device (only the Serial route with an RST pulse worked).
            if (tokens.ContainsKey("reboot"))
            {
                outgoing.Add("{\"cmd\":\"reboot\"}");
                return outgoing;
            }

            if (tokens.ContainsKey("Save&Reboot"))
            {
                return outgoing; // handled in SendCommandAndAwaitOkAsync
            }

            // STOP:0 / ESTOP:0 are button-release events -> the serial firmware ignores them, and so does WS.
            if (tokens.TryGetValue("ESTOP", out var estopState) && estopState == "0") return outgoing;
            if (tokens.TryGetValue("STOP", out var stopState) && stopState == "0") return outgoing;

            if (tokens.ContainsKey("ESTOP"))
            {
                _activeJogAxis = null;
                _activeJogDirection = 0;
                outgoing.Add("{\"cmd\":\"forceStop\",\"data\":{}}");
                return outgoing;
            }

            if (tokens.ContainsKey("STOP"))
            {
                _activeJogAxis = null;
                _activeJogDirection = 0;
                outgoing.Add("{\"cmd\":\"stop\",\"data\":{}}");
                return outgoing;
            }

            if (tokens.ContainsKey("ReER"))
            {
                outgoing.Add("{\"cmd\":\"resetError\",\"data\":{}}");
                return outgoing;
            }

            if (tokens.ContainsKey("SetH")) { outgoing.Add("{\"cmd\":\"setHome\",\"data\":{}}"); return outgoing; }
            if (tokens.ContainsKey("RetH")) { outgoing.Add("{\"cmd\":\"returnHome\",\"data\":{}}"); return outgoing; }
            if (tokens.ContainsKey("RstH")) { outgoing.Add("{\"cmd\":\"resetHome\",\"data\":{}}"); return outgoing; }
            if (tokens.TryGetValue("SLvl", out var level))
            {
                if (int.TryParse(level, out var parsedLevel) && parsedLevel > 0)
                {
                    _speedLevelFromSnapshot = parsedLevel;
                }
                outgoing.Add($"{{\"cmd\":\"speedLevel\",\"data\":{{\"level\":{ParseNumber(level)}}}}}");
                return outgoing;
            }

            // ---- Relative move mode / parameters (serial protocol only) ----
            // Remembered INTERNALLY **and** pushed to the firmware with saveConfig (exactly like the Web UI in
            // saveRelativeSettings()). It used to be remembered internally only, so the backend stayed in Jog mode and
            // the monitoring web client never knew -> it kept showing Jog although the PC had switched to Relative.
            // Once pushed, the firmware broadcasts {"relative":{...}} -> every client syncs.
            if (tokens.TryGetValue("JoRe", out var joRe))
            {
                _relativeMode = joRe != "0";
                PushRelativeSettings();
                return outgoing;
            }
            if (tokens.TryGetValue("ReDe", out var reDe))
            {
                _relativeDegrees = ParseIntOrZero(reDe);
                PushRelativeSettings();
                return outgoing;
            }
            if (tokens.TryGetValue("ReAM", out var reAm))
            {
                _relativeMinutes = ParseIntOrZero(reAm);
                PushRelativeSettings();
                return outgoing;
            }
            if (tokens.TryGetValue("ReAS", out var reAs))
            {
                _relativeSeconds = ParseIntOrZero(reAs);
                PushRelativeSettings();
                return outgoing;
            }

            // ---- Arrow buttons: continuous jog (move) or a single step (moveRelative) ----
            if (tokens.TryGetValue("MAzL", out var azL)) return HandleArrow("az", -1, azL);
            if (tokens.TryGetValue("MAzR", out var azR)) return HandleArrow("az", 1, azR);
            if (tokens.TryGetValue("MAlU", out var alU)) return HandleArrow("alt", 1, alU);
            if (tokens.TryGetValue("MAlD", out var alD)) return HandleArrow("alt", -1, alD);

            // ---- WiFi password query: send `getConfig` to READ FROM THE DEVICE (like `STAp:?` /
            //      `APpa:?` on Serial). The password is NOT in the snapshot/broadcast, so it cannot be answered
            //      from cache - the `configRead` answer is pushed back into the firmware text stream
            //      (see the `configRead` branch in HandleMessage) so the UI gets the real value.
            if (tokens.TryGetValue("APpa", out var apPassQuery) && apPassQuery == "?")
            {
                outgoing.Add("{\"cmd\":\"getConfig\",\"data\":{\"keys\":[\"wifi_ap\"]}}");
                return outgoing;
            }
            if (tokens.TryGetValue("STAp", out var staPassQuery) && staPassQuery == "?")
            {
                outgoing.Add("{\"cmd\":\"getConfig\",\"data\":{\"keys\":[\"pass\"]}}");
                return outgoing;
            }

            // ---- ALIGN ----
            // The Serial protocol splits this into two clearly different jobs:
            //   (a) AzED/AzEM/AzES/AzDi (+ Al...) = WRITE the error value to FRAM, no motor moves.
            //       The dock sends this line whenever the user edits an error box.
            //   (b) AzAN:1 / AlAN:1 / AAll:1    = TRIGGER a move using the stored value.
            // WebSocket equivalents: (a) saveConfig{align:{...}}  (b) align{ra_error,dec_error,simultaneous}.
            // Both used to be merged into one `align` command -> typing a number into an error box already moved the mount.
            var setterAz = AlignAzSetterKeys.Any(tokens.ContainsKey);
            var setterAlt = AlignAltSetterKeys.Any(tokens.ContainsKey);
            var triggerAz = tokens.ContainsKey("AAll") || tokens.ContainsKey("AzAN");
            var triggerAlt = tokens.ContainsKey("AAll") || tokens.ContainsKey("AlAN");

            if (setterAz || setterAlt || triggerAz || triggerAlt)
            {
                // The error of EACH axis = the token just sent (if any) + the value still stored on the device.
                // The Serial protocol writes each field on its own (AzED/AzEM/AzES/AzDi all write FRAM immediately), so
                // a field missing from the command has to keep its value and must not be cleared to 0.
                var az = MergeAlignParts(tokens, "Az", _alignAzParts);
                var alt = MergeAlignParts(tokens, "Al", _alignAltParts);
                _alignAzParts = az;
                _alignAltParts = alt;

                if (setterAz || setterAlt)
                {
                    var saveAlign = new
                    {
                        origin = "pcPlugin",
                        align = new
                        {
                            az = new { d = az.D, m = az.M, s = az.S, dir = az.Dir },
                            alt = new { d = alt.D, m = alt.M, s = alt.S, dir = alt.Dir }
                        }
                    };
                    outgoing.Add(JsonSerializer.Serialize(new { cmd = "saveConfig", data = saveAlign }));
                }

                if (triggerAz || triggerAlt)
                {
                    // An axis that is not triggered is sent as 0 so the firmware leaves that axis alone
                    // (exactly like Serial: AzAN moves AZ only, AlAN moves ALT only).
                    var azArcSec = triggerAz ? AlignArcSeconds(az) : 0;
                    var altArcSec = triggerAlt ? AlignArcSeconds(alt) : 0;
                    var simultaneous = tokens.ContainsKey("AAll") || (triggerAz && triggerAlt);
                    var json = $"{{\"cmd\":\"align\",\"data\":{{\"ra_error\":{azArcSec.ToString("0.###", CultureInfo.InvariantCulture)}," +
                               $"\"dec_error\":{altArcSec.ToString("0.###", CultureInfo.InvariantCulture)}," +
                               $"\"simultaneous\":{(simultaneous ? "true" : "false")}}}}}";
                    outgoing.Add(json);
                    _lastAlignSentUtc = DateTime.UtcNow;
                    _alignInFlight = true;
                    _sawBusySinceAlign = false;
                }
                return outgoing;
            }

            // ApplyConf (Serial: load the configuration held in RAM into the hardware) -> the WebSocket has no
            // "apply everything" command, so applyConfig is sent with ALL current settings of the plugin.
            if (tokens.ContainsKey("ApplyConf"))
            {
                var fullPayload = BuildConfigPayload(ParseCommandLine(
                    _serial.BuildConfigurationCommand(_settings, includeSaveAndReboot: false)));
                if (fullPayload.Count > 0)
                {
                    outgoing.Add(JsonSerializer.Serialize(new { cmd = "applyConfig", data = fullPayload }));
                }
                return outgoing;
            }

            // Configuration: applyConfig is sent straight away (no ack wait on the synchronous Send path).
            // Note: WiFi/AP groups passing through here are ignored by the firmware with the warning
            // "[SAVE&REBOOT required]" - exactly what the web UI does when APPLY is pressed.
            if (tokens.Keys.Any(k => ConfigKeyMap.ContainsKey(k)))
            {
                var payload = BuildConfigPayload(tokens);
                if (payload.Count > 0)
                {
                    outgoing.Add(JsonSerializer.Serialize(new { cmd = "applyConfig", data = payload }));
                }
                return outgoing;
            }

            // Read-only commands or commands with no WebSocket equivalent: the serial firmware ignores them too
            // (Home is read-only, STAi is ignored, APma/STAm/Scal/WSta/AzPH/AlPH are telemetry only)
            // -> nothing is sent and NO error is reported, to keep the log clean.
            if (tokens.Keys.All(k => ReadOnlyTelemetryKeys.Contains(k)))
            {
                return outgoing;
            }

            AppendLog($"WARNING: command not supported over Wireless: {string.Join(",", tokens.Keys)}");
            return outgoing;
        }

        private static readonly string[] AlignAzSetterKeys = { "AzED", "AzEM", "AzES", "AzDi" };
        private static readonly string[] AlignAltSetterKeys = { "AlED", "AlEM", "AlES", "AlDi" };

        /// <summary>Tokens that only exist in telemetry (or are ignored by the firmware) - nothing to translate, no error reported.</summary>
        private static readonly HashSet<string> ReadOnlyTelemetryKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "Home", "STAi", "APma", "STAm", "Scal", "WSta", "AzPH", "AlPH", "Mpos"
        };

        /// <summary>Splits a signed align error (arcsec) into d/m/s + dir exactly like the Serial protocol.</summary>
        private static (int D, int M, double S, bool Dir) MergeAlignParts(
            Dictionary<string, string> tokens, string prefix, (int D, int M, double S, bool Dir)? fallback)
        {
            var fallbackValue = fallback ?? (0, 0, 0.0, true);

            double? Token(string name) => tokens.TryGetValue(name, out var raw) ? ParseDecimal(raw) : null;
            var dRaw = Token(prefix + "ED");
            var mRaw = Token(prefix + "EM");
            var sRaw = Token(prefix + "ES");
            bool? dirRaw = tokens.TryGetValue(prefix + "Di", out var dirStr) ? dirStr != "0" : null;

            // Firmware: a negative value flips the direction and the number is taken as absolute (see handleSerialCommand).
            var negative = (dRaw ?? 0) < 0 || (mRaw ?? 0) < 0 || (sRaw ?? 0) < 0;

            var d = dRaw.HasValue ? (int)Math.Abs(dRaw.Value) : fallbackValue.D;
            var m = mRaw.HasValue ? Math.Abs(mRaw.Value) : fallbackValue.M;
            var s = sRaw.HasValue ? Math.Abs(sRaw.Value) : fallbackValue.S;
            var dir = dirRaw ?? (negative ? false : fallbackValue.Dir);
            return (d, (int)m, s, dir);
        }

        /// <summary>Signed align error (arcsec) from d/m/s + direction - used by the WebSocket align command.</summary>
        private static double AlignArcSeconds((int D, int M, double S, bool Dir) parts)
            => ((parts.D * 3600.0) + (parts.M * 60.0) + parts.S) * (parts.Dir ? 1 : -1);

        /// <summary>
        /// Translates one press/release of a dock arrow button into a WebSocket command.
        /// - Jog mode: press = `move` (continuous, NEVER re-sent because the firmware refuses while one runs),
        ///   release = `stop`.
        /// - Relative mode: press = `moveRelative` (one step), release = nothing (matching the serial firmware).
        /// </summary>
        private List<string> HandleArrow(string axis, int direction, string state)
        {
            var outgoing = new List<string>();

            if (state == "0")
            {
                _activeJogAxis = null;
                _activeJogDirection = 0;
                if (!_relativeMode)
                {
                    // Releasing the jog = a smooth DECELERATION of that axis (like Serial MAzL:0).
                    // It is always sent, even when the plugin never saw the press - stopping is safer.
                    outgoing.Add($"{{\"cmd\":\"stopMove\",\"data\":{{\"axis\":\"{axis}\"}}}}");
                }
                return outgoing;
            }

            if (_relativeMode)
            {
                var angle = _relativeDegrees + (_relativeMinutes / 60.0) + (_relativeSeconds / 3600.0);
                if (angle < 0) angle = 0;
                outgoing.Add($"{{\"cmd\":\"moveRelative\",\"data\":{{\"axis\":\"{axis}\",\"direction\":{direction}," +
                             $"\"angle\":{angle.ToString("0.####", CultureInfo.InvariantCulture)},\"speed\":{_speedLevelFromSnapshot}}}}}");
                return outgoing;
            }

            // Continuous jog: the watchdog repeats (every 250 ms) for the same direction are dropped.
            if (_activeJogAxis == axis && _activeJogDirection == direction)
            {
                return outgoing;
            }

            _activeJogAxis = axis;
            _activeJogDirection = direction;
            outgoing.Add($"{{\"cmd\":\"move\",\"data\":{{\"axis\":\"{axis}\",\"direction\":{direction},\"speed\":{_speedLevelFromSnapshot}}}}}");
            return outgoing;
        }

        private static int ParseIntOrZero(string value)
            => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

        /// <summary>
        /// Whether an `alert` is about a motion limit (soft/hard limit, refused align/relative) -
        /// these warnings ALWAYS come with an error code in the ERROR telemetry, so they get no separate toast
        /// (see the note on the `alert` branch in HandleIncoming).
        /// </summary>
        private static bool IsLimitAlert(string alert)
            => alert.Contains("limit", StringComparison.OrdinalIgnoreCase);

        /// <summary>Pushes a reply line "as if it came from the device" into the pipeline (and the ISerialLink adapter).</summary>
        private void InjectDeviceLine(string line)
        {
            try { LineReceived?.Invoke(line); } catch { }
            _serial.InjectIncomingText(line + "\n");
        }

        private async Task SendJsonAsync(string json, CancellationToken token)
        {
            var socket = _socket;
            if (socket == null || socket.State != WebSocketState.Open)
            {
                throw new InvalidOperationException("WebSocket is not open.");
            }

            var bytes = Encoding.UTF8.GetBytes(json);
            await _sendLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        // ==================================================================
        // SYSTEM LOG (like the Web UI System Log table)
        // Firmware/plugin messages only: timestamp + colour by keyword,
        // newest line on top, 50 lines at most. No raw TX/RX frames.
        // ==================================================================
        private const int SystemLogMaxEntries = 50;

        public ObservableCollection<SystemLogEntry> SystemLog { get; } = new();

        /// <summary>
        /// Writes PLUGIN diagnostics to the NINA log - they do NOT go into the System log table.
        /// The System log holds DEVICE messages only (exactly like the Web UI System Log table);
        /// adding plugin text here would make it differ from the web.
        /// </summary>
        private void AppendLog(string text)
        {
            try
            {
                Logger.Info($"[MLAstro][WS] {text}");
            }
            catch { }
        }

        /// <summary>Adds a line to the System log (noise filtered + coloured like the Web UI).</summary>
        public void AddSystemLog(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            // The Web UI drops this line because it appears far too often and is not useful.
            if (message.Contains("Manual stop sequence completed. Hardlimit re-enabled."))
            {
                return;
            }

            var entry = new SystemLogEntry(message, ClassifyLogLevel(message), DateTime.Now);

            void Add()
            {
                SystemLog.Insert(0, entry); // newest on top
                while (SystemLog.Count > SystemLogMaxEntries)
                {
                    SystemLog.RemoveAt(SystemLog.Count - 1);
                }
            }

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(Add));
            }
            else
            {
                Add();
            }
        }

        public void ClearSystemLog()
        {
            void Clear() => SystemLog.Clear();

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(Clear));
            }
            else
            {
                Clear();
            }
        }

        /// <summary>CSV content of the System log (oldest first) for Export CSV.</summary>
        public string BuildSystemLogCsv()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Time,Level,Message");
            foreach (var entry in SystemLog.Reverse())
            {
                var message = entry.Message.Replace("\"", "\"\"");
                sb.AppendLine($"{entry.Time},{entry.Level},\"{message}\"");
            }
            return sb.ToString();
        }

        /// <summary>Colour classification following the Web UI rules (appendLog).</summary>
        private static SystemLogLevel ClassifyLogLevel(string message)
        {
            if (message.Contains("[SAVE&REBOOT required]")) return SystemLogLevel.RebootRequired;
            if (message.Contains("[Apply]")) return SystemLogLevel.Apply;
            if (message.Contains("Reset by User")) return SystemLogLevel.Reset;

            if (message.Contains("CRITICAL") || message.Contains("ERROR") || message.Contains("Error") || message.Contains("failed")
                || message.Contains("Short to Ground") || message.Contains("Over Temperature") || message.Contains("Hardlimit reached"))
                return SystemLogLevel.Critical;

            if (message.Contains("WARNING") || message.Contains("limit") || message.Contains("Limit") || message.Contains("Hit")
                || message.Contains("Pre-Warn") || message.Contains("ALIGN ERROR"))
                return SystemLogLevel.Warning;

            if (message.Contains("COMPLETED") || message.Contains("Success") || message.Contains("saved"))
                return SystemLogLevel.Success;

            return SystemLogLevel.Info;
        }

        private static bool TryGetString(JsonElement root, string name, out string value)
        {
            value = string.Empty;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var prop)) return false;
            if (prop.ValueKind != JsonValueKind.String) return false;
            value = prop.GetString() ?? string.Empty;
            return true;
        }

        private static bool TryGetBool(JsonElement root, string name, out bool value)
        {
            value = false;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var prop)) return false;
            if (prop.ValueKind == JsonValueKind.True) { value = true; return true; }
            if (prop.ValueKind == JsonValueKind.False) { value = false; return true; }
            return false;
        }

        private static bool TryGetInt(JsonElement root, string name, out int value)
        {
            value = 0;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var prop)) return false;
            if (prop.ValueKind != JsonValueKind.Number) return false;
            return prop.TryGetInt32(out value);
        }

        private static bool TryGetDouble(JsonElement root, string name, out double value)
        {
            value = 0;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var prop)) return false;
            if (prop.ValueKind == JsonValueKind.Number) return prop.TryGetDouble(out value);
            if (prop.ValueKind == JsonValueKind.String)
            {
                return double.TryParse(prop.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
            }
            return false;
        }

        private void OnPropertyChanged([CallerMemberName] string propertyName = null!)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        public void Dispose()
        {
            try { Disconnect(); } catch { }
            try
            {
                if (ReferenceEquals(_serial.WirelessProxy, this))
                {
                    _serial.WirelessProxy = null;
                }
            }
            catch { }
            if (Instance == this) Instance = null;
        }
    }
}
