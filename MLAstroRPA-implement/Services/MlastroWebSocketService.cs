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
    /// Transport WIRELESS cho MLAstroRPA: kết nối tới thiết bị qua WebSocket
    /// (mặc định ws://MLAstroRPA.local/ws, có thể nhập IP trực tiếp khi mDNS không hoạt động).
    ///
    /// Đặc điểm:
    ///  - Dùng CÙNG endpoint /ws như Web UI, phân biệt bằng từ khóa handshake "MLAstroRPA-TC"
    ///    (giống serial) → firmware trao quyền điều khiển + monitor cho PC và khóa điều khiển Web.
    ///  - Telemetry JSON được chuyển ngược thành ĐÚNG định dạng text của firmware serial rồi bơm
    ///    vào <see cref="SerialConnectionService.InjectIncomingText"/> → toàn bộ parser/UI hiện có
    ///    (CONTROL + CONFIGURATION + dock TPPA) hoạt động y như khi dùng cổng COM.
    ///  - Lệnh dạng text của firmware serial được dịch sang JSON của WebSocket API.
    /// </summary>
    public sealed class MlastroWebSocketService : INotifyPropertyChanged, IDisposable
    {
        public const string HandshakeKey = "MLAstroRPA-TC";

        private const int ReceiveChunkSize = 16 * 1024;
        private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan ConfigAckTimeout = TimeSpan.FromSeconds(8);

        // Chính sách THỬ LẠI khi kết nối: chỉ thử vài lần trong 1 khoảng NGẮN (mặc định 5 s) rồi
        // báo failed. Địa chỉ sai / mDNS không resolve mà retry vô hạn thì UI treo và người dùng
        // không bao giờ biết kết nối đã thất bại.
        private static readonly TimeSpan ConnectAttemptWindow = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan ConnectRetryDelay = TimeSpan.FromMilliseconds(700);
        // Timeout cho TỪNG lần: mDNS/DNS có thể treo lâu khi hostname sai, và TCP connect tới IP
        // sai subnet có thể treo ~20 s.
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
        // Giá trị cấu hình nằm ở CẤP CAO NHẤT của frame (không thuộc section nào) - vd thông tin STA
        // (ssid/ip/sta_mac) vì firmware giữ tên cũ cho web UI đọc.
        private readonly Dictionary<string, object> _snapshotScalars = new(StringComparer.OrdinalIgnoreCase);
        // Sai số align (d/m/s + hướng) gần nhất plugin BIẾT: dùng khi lệnh align chỉ có cờ kích hoạt
        // (AzAN/AlAN/AAll) hoặc khi lệnh ghi chỉ gửi 1 phần trường — giống Serial (firmware dùng/giữ
        // giá trị đã lưu trong FRAM).
        private (int D, int M, double S, bool Dir)? _alignAzParts;
        private (int D, int M, double S, bool Dir)? _alignAltParts;
        private DateTime _lastAlignSentUtc = DateTime.MinValue;
        private bool _alignInFlight;
        private bool _sawBusySinceAlign;
        private int _speedLevelFromSnapshot = 3;
        // Phiên bản firmware đã log lần cuối: frame `config_pushed` cũng mang `fw_ver` nên nếu không
        // so sánh thì mỗi lần đổi cài đặt lại ghi thêm 1 dòng "Firmware: …" vào System log.
        private string? _firmwareVersionFromSnapshot;

        // Trạng thái "chế độ" của dock (JoRe/ReDe/ReAM/ReAS chỉ có ở giao thức serial):
        // WebSocket không có lệnh tương đương nên phải ghi nhớ để dịch đúng arrow-press
        // thành `move` (jog liên tục) hay `moveRelative` (dịch một góc).
        private bool _relativeMode;
        private int _relativeDegrees;
        private int _relativeMinutes;
        private int _relativeSeconds;

        // Jog đang chạy: dock gửi lại lệnh mỗi 250 ms (watchdog) nhưng firmware WS từ chối
        // mọi lệnh motion khi đang chạy → phải bỏ các lần gửi lặp để tránh spam alert.
        private string? _activeJogAxis;
        private int _activeJogDirection;

        /// <summary>Singleton để controller và driver TPPA dùng CHUNG một phiên WS (firmware chỉ cho 1 PC).</summary>
        public static MlastroWebSocketService? Instance { get; private set; }

        /// <summary>Mọi dòng text đã tổng hợp (telemetry / ok / AAll:COMPLETED): cho adapter ISerialLink của TPPA.</summary>
        public event Action<string>? LineReceived;

        /// <summary>Trạng thái kết nối đổi (arg = IsConnected).</summary>
        public event Action<bool>? StateChanged;

        /// <summary>Nhấn STOP/E-STOP bên MLAstro (hoặc mất kết nối) → TPPA phải dừng PA.</summary>
        public event Action<string>? StopRequested;

        public event PropertyChangedEventHandler? PropertyChanged;

        public MlastroWebSocketService(PluginSettings settings, SerialConnectionService serial)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _serial = serial ?? throw new ArgumentNullException(nameof(serial));

            // Trở thành facade cho UI/dock: từ giờ SerialConnectionService báo trạng thái + gửi lệnh
            // qua chính phiên WebSocket này khi người dùng chọn Wireless connection.
            _serial.WirelessProxy = this;

            Instance = this;
        }

        // ==================================================================
        // Trạng thái
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
        // ĐƯỜNG VÀO + CHẤT LƯỢNG STA (firmware báo)
        //   LinkPath   : "AP" = PC đang join hotspot của thiết bị; "STA" = đi qua router.
        //                Firmware tính theo remoteIP của TỪNG client ở frame handshakeResult.
        //   StaQuality : 0 = chưa vào router, 1 = có router nhưng không internet, 2 = có internet
        //                (field `sta_qual`, dò bằng TCP probe trong firmware).
        //   StaIp      : IP LAN mà router cấp cho ESP32 (field `sta_ip`).
        // ==================================================================
        private string _linkPath = string.Empty;
        public string LinkPath
        {
            // Đọc qua IsConnected: rớt kết nối là tự rỗng, không cần reset ở mọi nhánh thoát.
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

        // AP của THIẾT BỊ (hotspot ESP32 phát ra): đã lên và có IP chưa + IP hiện tại.
        // Dùng cho dòng "AP: Connected/Ready/Error <IP>" trên HeaderBar.
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

        // IP AP mới nhất firmware báo (field `ap_ip`); rỗng = chưa có.
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

            // LUÔN bắn event StopRequested — đây là kênh của transport TPPA-wireless
            // (MlastroWirelessSerial đăng ký event này, KHÔNG đăng ký external-stop listener).
            // Trước đây hàm return sớm khi chưa có external-stop listener ⇒ STOP/E-STOP bấm trên
            // plugin MLAstro lúc TPPA đang chạy Wireless KHÔNG tới được TPPA (không toast, không
            // dừng routine). Bug user báo 2026-09-16 (log chỉ có dòng NotifyExternalStop).
            try { StopRequested?.Invoke(reason); } catch { }
        }

        public void SetExternalPauseQuery(bool pause)
        {
            // Wireless: telemetry do firmware đẩy định kỳ, không có poll "?" → không cần pause.
            Logger.Info($"[MLAstro][WS] SetExternalPauseQuery({pause}) ignored (fw push telemetry).");
        }

        // ==================================================================
        // Kết nối / ngắt
        // ==================================================================
        public async Task<bool> ConnectAsync(CancellationToken token = default)
        {
            if (IsConnected) return true;

            // Phiên mới: xoá bảng log của phiên trước (giống refresh trang Web UI) để bảng chỉ
            // hiển thị sự kiện của lần kết nối này. Firmware cũng không replay log cũ cho PC nữa.
            ClearSystemLog();

            var host = ConfiguredAddress.Trim();
            var port = _settings.MlaPort <= 0 ? 80 : _settings.MlaPort;
            var path = string.IsNullOrWhiteSpace(_settings.MlaPath) ? "/ws" : _settings.MlaPath;
            if (!path.StartsWith("/")) path = "/" + path;

            ConnectionStatus = $"Resolving {host}...";
            HandshakeStatus = string.Empty;
            AppendLog($"Resolving {host} ...");

            // ---- Thử resolve + connect vài lần trong ConnectAttemptWindow rồi BÁO FAILED ----
            var deadline = DateTime.UtcNow + ConnectAttemptWindow;
            var isIpLiteral = IPAddress.TryParse(host, out _);
            var attempt = 0;
            string? lastError = null;
            ClientWebSocket? socket = null;
            Uri? uri = null;

            while (socket == null)
            {
                attempt++;

                // Hết cửa sổ thử lại thì KHÔNG mở thêm lần nào nữa (nếu không, mỗi lần có thể chờ
                // thêm ConnectAttemptTimeout → tổng thời gian vượt xa 5 s).
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

            // Đọc init snapshot + chờ handshake
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

            // Phiên mới: xoá trạng thái lỗi còn sót (wireless không nhận dòng ERROR: như serial).
            try { _serial.ResetErrorStateForNewSession(); } catch { }

            AppendLog($"Handshake: OK! PC has control; Web UI locked (monitoring only).");
            Logger.Info($"[MLAstro][WS] Connected and handshaked on {connectedHost}:{port}{path}");
            return true;
        }

        private static async Task<IPAddress?> ResolveMdnsAsync(string host, CancellationToken token)
        {
            try
            {
                // DNS/mDNS có thể treo rất lâu khi hostname sai → chặn trong ResolveTimeout rồi
                // trả null để vòng thử lại của ConnectAsync quyết định (thay vì treo vô hạn).
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
                    // Nhả quyền êm để Web UI mở khóa ngay (không cần F5)
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
        /// Reboot thiết bị qua WebSocket — hoạt động ĐÚNG như nút REBOOT trên Web UI: gửi
        /// <c>{"cmd":"reboot"}</c> để firmware broadcast <c>sys_status=REBOOTING</c> rồi <c>ESP.restart()</c>.
        /// ⚠ KHÔNG gửi <c>releaseControl</c> trước đó: sau khi nhả quyền, client không còn là
        /// PC-controller nên firmware từ chối lệnh reboot (trả "locked") ⇒ nút Reset ESP32 trên
        /// đường Wireless im lặng không làm gì. Reboot tự xoá phiên PC ở phía thiết bị nên không cần nhả.
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

        public bool QueryTelemetry() => IsConnected; // firmware tự đẩy telemetry ~250 ms

        // ==================================================================
        // Nhận dữ liệu
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
                    // Không còn điều khiển được thiết bị → TPPA phải dừng PA đang chạy.
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

                // Log/alert do firmware đẩy lên
                if (TryGetString(root, "log", out var logMsg))
                {
                    AddSystemLog(logMsg); // nội dung chính của bảng System log (giống Web UI)
                }
                if (TryGetString(root, "alert", out var alert))
                {
                    // Web UI hiện `alert` bằng MODAL và KHÔNG ghi vào bảng System log (xem
                    // data/script.js: showModal('System Message', data.alert) — không gọi appendLog).
                    // Plugin giữ đúng tương đương: file NINA log (và toast, xem dưới), KHÔNG đưa vào
                    // System log → bảng System log của plugin luôn giống hệt bảng System Log của Web UI.
                    AppendLog($"ALERT: {alert}");

                    // Cảnh báo LIÊN QUAN GIỚI HẠN (soft/hard limit, align/relative bị từ chối) KHÔNG
                    // toast nữa: firmware đã báo CÙNG một sự việc bằng mã lỗi trong ERROR telemetry
                    // (AzSL/AlSL/AzHL/AlHL/RfJog*/RfAln*) → trước đây bấm jog vào soft-limit hiện 2 hộp
                    // thoại ("Soft Limit Reached! AZ axis stopped at configured limit." của `alert` và
                    // "AZ soft limit reached" của mã lỗi AzSL).
                    // Kênh mã lỗi được chọn vì đây mới là kênh có ở MỌI môi trường điều khiển (Serial
                    // không có `alert`) → câu chữ + số lượng thông báo giống nhau dù dùng cáp hay WS.
                    // Nội dung `alert` chi tiết hơn (vd hướng cần nhấn để thoát hard-limit) vẫn được
                    // ghi vào file log NINA qua AppendLog ở trên.
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
                        // Đường vào (AP/STA) — firmware tính theo remoteIP của chính client này.
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
                        // Web UI làm đúng như vậy: appendLog(data.reason) — KHÔNG thêm tiền tố.
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
                        // Trả lời của `getConfig` (đọc riêng password WiFi theo yêu cầu). Bơm vào luồng
                        // text của firmware như thể thiết bị vừa trả lời `STAp:…` / `APpa:…` → đường
                        // serial sẽ tự cập nhật settings + UI (giống hệt khi kết nối bằng cáp USB).
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

                // 2) Ack của config
                if (TryGetString(root, "status", out var status))
                {
                    if (status == "configSaved" || status == "configApplied")
                    {
                        _configAckTcs?.TrySetResult(status);
                        return;
                    }
                }

                // 3) Snapshot cấu hình: frame đầu tiên sau khi kết nối VÀ mọi frame `config_pushed`
                //    (firmware push lại khi cấu hình đổi từ bất kỳ đường nào) → luôn cache lại.
                //    ⚠ Nhánh này CŨNG bắt các frame chỉ chứa MỘT setting (vd `{"speedLevel":2}` khi đổi
                //    tốc độ, hay `handshakeResult` có `fw_ver`) vì firmware gửi delta cho từng setting.
                //    Vì vậy mọi hàm con ở đây BẮT BUỘC theo nguyên tắc "chỉ ghi khi field CÓ MẶT":
                //    thiếu field KHÔNG có nghĩa là thiết bị báo rỗng.
                //    (Password WiFi KHÔNG nằm trong snapshot — đọc riêng bằng lệnh `getConfig`.)
                if (TryGetInt(root, "speedLevel", out _) || TryGetString(root, "fw_ver", out _))
                {
                    CacheSnapshotSections(root);
                    ApplyRelativeState(root); // Jog/Relative đang lưu trên device là nguồn sự thật
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

                // 3b) Firmware broadcast trạng thái Relative ({"relative":{...}}) - phát ra khi
                //     BẤT KỲ client nào (Web hoặc PC plugin) đổi chế độ/tham số Relative.
                //     Ghi lại để telemetry (JoRe/ReDe/ReAM/ReAS) + dock luôn khớp với backend.
                if (ApplyRelativeState(root))
                {
                    return;
                }

                // 4) Trạng thái mã lỗi do firmware gửi qua WebSocket (edge-triggered, giống dòng
                //    "ERROR:..." của Serial). Không có nhánh này thì bảng Alarm History trống trơn
                //    vì đường Serial không được dùng khi kết nối Wireless.
                if (TryGetString(root, "error", out var errorLine) && !string.IsNullOrWhiteSpace(errorLine))
                {
                    var line = errorLine.StartsWith("ERROR:", StringComparison.Ordinal) ? errorLine : "ERROR:" + errorLine;
                    // CHỈ nạp vào bảng Alarm History + file NINA log. Web UI cũng KHÔNG ghi dòng lỗi vào
                    // System log (chỉ modal), nên đứng thêm dòng tổng hợp vào đây để hai bảng log giống nhau.
                    AppendLog(line);
                    _serial.InjectIncomingText(line);   // -> ProcessErrorTelemetry -> Alarm History
                    return;
                }

                // 5) Telemetry định kỳ: KHÔNG ghi vào System log (250 ms/lần sẽ làm ngập log),
                //    chỉ chuyển thành telemetry text cho pipeline UI + driver TPPA.
                //    Chỉ coi là telemetry khi có trường vị trí (các frame chỉ có sys_status như
                //    STOPPED/REBOOTING không được phép ghi đè vị trí hiển thị).
                if (root.TryGetProperty("pos_az", out _))
                {
                    var line = BuildSerialTelemetryLine(root);
                    if (!string.IsNullOrEmpty(line))
                    {
                        // Cho adapter ISerialLink của TPPA (ReadLine/ReadExisting)
                        try { LineReceived?.Invoke(line); } catch { }

                        // Cho pipeline UI dùng chung với serial (TelemetryParser + dock)
                        _serial.InjectIncomingText(line + "\n");

                        // Chuyển tiếp sự kiện hoàn tất align cho controller UI + driver TPPA.
                        // Chỉ coi là xong khi: firmware báo ALIGN_COMPLETED, HOẶC READY mà trước đó
                        // đã thấy trạng thái đang chạy (tránh READY thoáng qua ngay sau khi gửi lệnh),
                        // HOẶC READY sau 1.5 s (các bước dịch rất nhỏ không có pha ALIGNING).
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
        /// Section cấu hình của frame snapshot/broadcast được cache để TỔNG HỢP TELEMETRY và để tra cứu
        /// khi dịch lệnh. PHẢI khớp với fillConfigSections() bên firmware; thêm cài đặt mới ở firmware
        /// thì thêm tên section ở đây (+ token tương ứng trong AppendSnapshotTokens).
        /// </summary>
        private static readonly string[] SnapshotSectionNames =
            { "limits", "motor", "backlash", "wifi_ap", "serial", "align", "align_mode" };

        /// <summary>Giá trị cấp cao nhất của frame snapshot cần cho telemetry text (thông tin STA).</summary>
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

            // Sai số align đang LƯU trong thiết bị là nguồn sự thật (dùng khi lệnh align không kèm giá trị).
            if (_snapshotSections.TryGetValue("align", out var alignSnap))
            {
                _alignAzParts = AlignPartsFromSection(alignSnap, "az");
                _alignAltParts = AlignPartsFromSection(alignSnap, "alt");
            }
        }

        /// <summary>Làm phẳng một object JSON thành "key" hoặc "cha.con" để tra cứu đơn giản.</summary>
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

        /// <summary>Sai số align (d/m/s + hướng) từ section "align" đã cache: {az:{d,m,s,dir}}.</summary>
        private static (int D, int M, double S, bool Dir)? AlignPartsFromSection(Dictionary<string, object> align, string axis)
        {
            if (!align.ContainsKey(axis + ".d") && !align.ContainsKey(axis + ".s")) return null;
            var d = ParseDecimal(Get(align, axis + ".d"));
            var m = ParseDecimal(Get(align, axis + ".m"));
            var s = ParseDecimal(Get(align, axis + ".s"));
            // PHẢI dùng GetFlag(): `dir` trong JSON là bool, mà Convert.ToString(false) = "False" nên
            // cách so sánh kiểu `Get(...) != "0"` sẽ đọc cờ false thành TRUE → hướng align luôn là
            // "Right/Up" và người dùng không thể đảo chiều bằng nút toggle trên PC client.
            var dir = GetFlag(align, axis + ".dir");
            return ((int)d, (int)m, s, dir);
        }

        /// <summary>
        /// Đọc một cờ trong dict đã cache (JSON bool / số 0-1 / chuỗi "1"|"true") thành bool.
        /// Dùng cho MỌI trường kiểu cờ — không so sánh chuỗi với "0" vì bool→string là "True"/"False".
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

        /// <summary>Tổng hợp telemetry JSON thành đúng định dạng text của firmware serial.</summary>
        private string BuildSerialTelemetryLine(JsonElement root)
        {
            var status = TryGetString(root, "sys_status", out var s) ? s : "READY";
            var movedAz = TryGetDouble(root, "align_moved_az", out var az) ? az : 0;
            var movedAlt = TryGetDouble(root, "align_moved_alt", out var alt) ? alt : 0;

            var tokens = new List<string>();
            // Scal:1 = hệ số scale của khung telemetry đầy đủ (giống firmware serial).
            AddToken(tokens, "Scal", "1");
            AddToken(tokens, "SLvl", _speedLevelFromSnapshot.ToString(CultureInfo.InvariantCulture));
            AddToken(tokens, "WSta", TryGetDouble(root, "rssi", out var rssi) && rssi > -1000 ? "1" : "0");

            // Chất lượng đường STA + IP LAN của thiết bị (firmware ≥ 1.7.0). Đẩy tiếp thành token
            // WQu / STAi để dock dùng CHUNG một đường parse với khi cắm cáp Serial.
            // Firmware cũ (< 1.7.0) không gửi `sta_qual` → suy ra mức tối thiểu từ RSSI (đang vào
            // router ⇒ 1) để dòng STA không hiện sai thành "none".
            StaQuality = TryGetInt(root, "sta_qual", out var staQual)
                ? staQual
                : (rssi > -1000 ? 1 : 0);
            if (TryGetString(root, "sta_ip", out var staIp) && !string.IsNullOrWhiteSpace(staIp))
            {
                // Ghi vào snapshot scalar "ip" ⇒ AppendSnapshotTokens phát token STAi với IP MỚI NHẤT
                // (IP trong snapshot có thể cũ nếu DHCP cấp địa chỉ khác mà cấu hình không đổi).
                _snapshotScalars["ip"] = staIp;
            }
            AddToken(tokens, "WQu", StaQuality.ToString(CultureInfo.InvariantCulture));

            // AP của thiết bị: đã lên/có IP chưa (token APrd) + IP AP (token APip, xem
            // AppendSnapshotTokens — IP live được ưu tiên hơn giá trị trong snapshot cấu hình).
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

            // Chế độ dịch chuyển tương đối: giao thức Serial có JoRe/ReDe/ReAM/ReAS là TRẠNG THÁI,
            // còn WebSocket không có lệnh tương đương nên plugin tự ghi nhớ. Phải đưa vào telemetry,
            // nếu không dock sẽ nhận IsRelativeMode = false sau mỗi gói (toggle tự tắt ngay khi bật).
            AddToken(tokens, "JoRe", _relativeMode ? "1" : "0");
            AddToken(tokens, "ReDe", _relativeDegrees.ToString(CultureInfo.InvariantCulture));
            AddToken(tokens, "ReAM", _relativeMinutes.ToString(CultureInfo.InvariantCulture));
            AddToken(tokens, "ReAS", _relativeSeconds.ToString(CultureInfo.InvariantCulture));

            AppendSnapshotTokens(tokens);

            return $"<{status}|Mpos:{movedAz.ToString("0.#####", CultureInfo.InvariantCulture)},{movedAlt.ToString("0.#####", CultureInfo.InvariantCulture)}|>{string.Join(",", tokens)}";
        }

        private void AppendSnapshotTokens(List<string> tokens)
        {
            // Định dạng số ở đây PHẢI khớp snprintf() của firmware (AzL1:%.1f, AzSD:%.5f,
            // AzED:%.0f, AzES:%.2f...) để dock/driver TPPA nhận đúng kiểu dữ liệu như khi dùng cáp Serial.
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
                // IP AP: ưu tiên `ap_ip` LIVE (firmware ≥ 1.7.0); snapshot chỉ là cấu hình đã lưu nên
                // có thể khác thực tế khi AP lên bằng giá trị fallback.
                AddToken(tokens, "APip", string.IsNullOrWhiteSpace(_apIp) ? Get(ap, "ip") : _apIp);
                AddToken(tokens, "APsu", Get(ap, "subnet"));
            }

            // Thông tin STA nằm ở CẤP CAO NHẤT của frame (firmware giữ tên cũ ip/ssid cho web UI).
            AddToken(tokens, "STAs", GetScalar("ssid"));
            AddToken(tokens, "STAm", GetScalar("sta_mac"));
            AddToken(tokens, "STAi", GetScalar("ip"));

            // Sai số align đang LƯU trong thiết bị (AzED/AzEM/AzES/AzDi + Al...). Không có thì các ô
            // nhập sai số trên dock/driver TPPA sẽ trắng sau mỗi gói telemetry.
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
        /// Định dạng giá trị trong dict theo mẫu số của firmware ("0.#" = %.1f, "0.#####" = %.5f,
        /// "1" = cờ 0/1). Giá trị không phải số (ssid/ip/mac) giữ nguyên.
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
        // Gửi lệnh (text protocol của firmware -> JSON WebSocket API)
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
                    // Lệnh không cần gửi (vd "?" vì telemetry do firmware đẩy)
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
        /// Đẩy chế độ/tham số Relative XUỐNG firmware, đúng như Web UI làm
        /// (saveRelativeSettings() → saveConfig). Nhờ vậy backend là nguồn sự thật duy nhất:
        /// firmware ghi FRAM rồi broadcast {"relative":{...}} cho MỌI client (Web + PC).
        /// </summary>
        private void PushRelativeSettings()
        {
            if (!IsConnected) return;

            try
            {
                var payload = new
                {
                    origin = "pcPlugin", // để ack configSaved không bị hiểu là ack của lượt web-save
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
        /// Đọc trạng thái Relative NGƯỢC từ backend (broadcast {"relative":{...}} hoặc init
        /// snapshot). Nhờ vậy plugin không bao giờ lệch với firmware/Web client.
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

            // WiFi (STA) / WiFi (AP) chỉ ghi được vào FRAM bằng saveConfig, và firmware sẽ TỰ REBOOT
            // ngay sau khi lưu nếu không có no_reboot → client không bao giờ nhận được ack configSaved.
            // Vì vậy luôn gửi kèm no_reboot:true rồi tự gửi lệnh reboot sau khi đã xác nhận (giống
            // cách web UI làm), tránh reboot khi chưa chắc đã lưu xong.
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
                // Firmware không tự reboot khi lưu config qua WS → gửi reboot như giao thức serial.
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

            // WiFi (AP) & WiFi (STA): firmware nhận 2 nhóm này trong saveConfig (ghi FRAM + reboot),
            // tương đương các lệnh APss/APpa/APip/APsu/STAs/STAp của giao thức Serial.
            // TRƯỚC ĐÂY bị bỏ qua → đổi WiFi/AP bằng Wireless không có tác dụng.
            ["APss"] = ("wifi_ap", "ssid", false),
            ["APpa"] = ("wifi_ap", "pass", false),
            ["APip"] = ("wifi_ap", "ip", false),
            ["APsu"] = ("wifi_ap", "subnet", false),
            ["STAs"] = ("wifi", "ssid", false),
            ["STAp"] = ("wifi", "pass", false),
        };

        /// <summary>Nhóm cấu hình chỉ áp dụng được khi LƯU (saveConfig) — WiFi/AP.</summary>
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

            // Telemetry query: firmware tự đẩy, không cần gửi
            if (tokens.Count == 1 && tokens.ContainsKey("?")) return outgoing;

            // Handshake text của serial: transport WS đã handshake khi kết nối
            if (tokens.ContainsKey("[MLAstroRPA-TC]")) return outgoing;

            if (tokens.TryGetValue("Disconnect", out _))
            {
                outgoing.Add("{\"cmd\":\"releaseControl\"}");
                return outgoing;
            }

            // `reboot` (giao thức text) → `{"cmd":"reboot"}`, giống hệt nút REBOOT trên Web UI.
            // Trước đây token này KHÔNG được map nên bị nuốt ⇒ nút Reset ESP32 trên đường Wireless
            // không reboot được thiết bị (chỉ đường Serial dùng xung RST mới chạy).
            if (tokens.ContainsKey("reboot"))
            {
                outgoing.Add("{\"cmd\":\"reboot\"}");
                return outgoing;
            }

            if (tokens.ContainsKey("Save&Reboot"))
            {
                return outgoing; // xử lý ở SendCommandAndAwaitOkAsync
            }

            // STOP:0 / ESTOP:0 = sự kiện nhả nút → firmware serial bỏ qua, WS cũng vậy.
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

            // ---- Chế độ / tham số dịch chuyển tương đối (chỉ có ở giao thức serial) ----
            // Ghi nhớ NỘI BỘ **và** đẩy xuống firmware bằng saveConfig (giống hệt Web UI trong
            // saveRelativeSettings()). Trước đây chỉ nhớ nội bộ nên backend vẫn ở chế độ Jog,
            // Web client (đang monitor) không hề biết → vẫn hiển thị Jog dù PC đã bật Relative.
            // Sau khi đẩy xuống, firmware broadcast {"relative":{...}} → mọi client đồng bộ.
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

            // ---- Nút mũi tên: jog liên tục (move) hoặc dịch một góc (moveRelative) ----
            if (tokens.TryGetValue("MAzL", out var azL)) return HandleArrow("az", -1, azL);
            if (tokens.TryGetValue("MAzR", out var azR)) return HandleArrow("az", 1, azR);
            if (tokens.TryGetValue("MAlU", out var alU)) return HandleArrow("alt", 1, alU);
            if (tokens.TryGetValue("MAlD", out var alD)) return HandleArrow("alt", -1, alD);

            // ---- Truy vấn password WiFi: gửi `getConfig` để ĐỌC TỪ THIẾT BỊ (giống `STAp:?` /
            //      `APpa:?` bên Serial). Password KHÔNG nằm trong snapshot/broadcast nên không thể trả
            //      lời từ cache — câu trả lời `configRead` sẽ được bơm ngược vào luồng text của firmware
            //      (xem nhánh xử lý `configRead` trong HandleMessage) nên UI nhận được giá trị thật.
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
            // Giao thức Serial tách làm 2 việc khác hẳn nhau:
            //   (a) AzED/AzEM/AzES/AzDi (+ Al...) = GHI giá trị sai số vào FRAM, KHÔNG chạy motor.
            //       Dock gửi dòng này mỗi khi người dùng sửa ô nhập sai số.
            //   (b) AzAN:1 / AlAN:1 / AAll:1    = KÍCH HOẠT dịch chuyển theo giá trị đã ghi.
            // WebSocket tương đương: (a) saveConfig{align:{...}}  (b) align{ra_error,dec_error,simultaneous}.
            // Trước đây gộp cả hai thành lệnh `align` → chỉ gõ số vào ô sai số cũng làm mount quay.
            var setterAz = AlignAzSetterKeys.Any(tokens.ContainsKey);
            var setterAlt = AlignAltSetterKeys.Any(tokens.ContainsKey);
            var triggerAz = tokens.ContainsKey("AAll") || tokens.ContainsKey("AzAN");
            var triggerAlt = tokens.ContainsKey("AAll") || tokens.ContainsKey("AlAN");

            if (setterAz || setterAlt || triggerAz || triggerAlt)
            {
                // Sai số của TỪNG trục = token vừa gửi (nếu có) + giá trị còn lại đang lưu trong thiết bị.
                // Giao thức Serial ghi từng trường riêng lẻ (AzED/AzEM/AzES/AzDi đều ghi FRAM ngay) nên
                // gửi thiếu trường nào thì trường đó phải giữ nguyên, không được xoá về 0.
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
                    // Trục không được kích hoạt thì gửi 0 để firmware không đụng tới trục đó
                    // (đúng như Serial: AzAN chỉ chạy AZ, AlAN chỉ chạy ALT).
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

            // ApplyConf (Serial: nạp cấu hình đang có trong RAM vào phần cứng) → WebSocket không có lệnh
            // "apply tất cả", nên gửi applyConfig với TOÀN BỘ cài đặt hiện tại của plugin.
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

            // Cấu hình: gửi thẳng applyConfig (không chờ ack ở đường Send đồng bộ).
            // Lưu ý: nhóm WiFi/AP đi qua đây sẽ bị firmware bỏ qua kèm cảnh báo
            // "[SAVE&REBOOT required]" — đúng như hành vi của web UI khi bấm APPLY.
            if (tokens.Keys.Any(k => ConfigKeyMap.ContainsKey(k)))
            {
                var payload = BuildConfigPayload(tokens);
                if (payload.Count > 0)
                {
                    outgoing.Add(JsonSerializer.Serialize(new { cmd = "applyConfig", data = payload }));
                }
                return outgoing;
            }

            // Lệnh chỉ-đọc hoặc không có tương đương ở WebSocket: firmware Serial cũng bỏ qua
            // (Home là read-only, STAi bị bỏ qua, APma/STAm/Scal/WSta/AzPH/AlPH chỉ là telemetry)
            // → không gửi gì và KHÔNG báo lỗi để tránh nhiễu log.
            if (tokens.Keys.All(k => ReadOnlyTelemetryKeys.Contains(k)))
            {
                return outgoing;
            }

            AppendLog($"WARNING: command not supported over Wireless: {string.Join(",", tokens.Keys)}");
            return outgoing;
        }

        private static readonly string[] AlignAzSetterKeys = { "AzED", "AzEM", "AzES", "AzDi" };
        private static readonly string[] AlignAltSetterKeys = { "AlED", "AlEM", "AlES", "AlDi" };

        /// <summary>Từ/khoá chỉ có ở telemetry (hoặc bị firmware bỏ qua) — không cần dịch, không báo lỗi.</summary>
        private static readonly HashSet<string> ReadOnlyTelemetryKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "Home", "STAi", "APma", "STAm", "Scal", "WSta", "AzPH", "AlPH", "Mpos"
        };

        /// <summary>Tách sai số align (arcsec, có dấu) thành d/m/s + dir đúng như giao thức Serial.</summary>
        private static (int D, int M, double S, bool Dir) MergeAlignParts(
            Dictionary<string, string> tokens, string prefix, (int D, int M, double S, bool Dir)? fallback)
        {
            var fallbackValue = fallback ?? (0, 0, 0.0, true);

            double? Token(string name) => tokens.TryGetValue(name, out var raw) ? ParseDecimal(raw) : null;
            var dRaw = Token(prefix + "ED");
            var mRaw = Token(prefix + "EM");
            var sRaw = Token(prefix + "ES");
            bool? dirRaw = tokens.TryGetValue(prefix + "Di", out var dirStr) ? dirStr != "0" : null;

            // Firmware: giá trị âm = đảo hướng, phần số lấy trị tuyệt đối (xem handleSerialCommand).
            var negative = (dRaw ?? 0) < 0 || (mRaw ?? 0) < 0 || (sRaw ?? 0) < 0;

            var d = dRaw.HasValue ? (int)Math.Abs(dRaw.Value) : fallbackValue.D;
            var m = mRaw.HasValue ? Math.Abs(mRaw.Value) : fallbackValue.M;
            var s = sRaw.HasValue ? Math.Abs(sRaw.Value) : fallbackValue.S;
            var dir = dirRaw ?? (negative ? false : fallbackValue.Dir);
            return (d, (int)m, s, dir);
        }

        /// <summary>Sai số align có dấu (arcsec) từ d/m/s + hướng — dùng cho lệnh align của WebSocket.</summary>
        private static double AlignArcSeconds((int D, int M, double S, bool Dir) parts)
            => ((parts.D * 3600.0) + (parts.M * 60.0) + parts.S) * (parts.Dir ? 1 : -1);

        /// <summary>
        /// Dịch một lần nhấn/nhả nút mũi tên của dock thành lệnh WebSocket.
        /// - Chế độ Jog: nhấn = `move` (liên tục, KHÔNG gửi lặp vì firmware từ chối khi đang chạy),
        ///   nhả = `stop`.
        /// - Chế độ Relative: nhấn = `moveRelative` (một góc), nhả = không làm gì (khớp firmware serial).
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
                    // Nhả jog = GIẢM TỐC mượt theo trục (giống Serial MAzL:0).
                    // Luôn gửi kể cả khi plugin không thấy lần nhấn trước đó — dừng an toàn hơn.
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

            // Jog liên tục: bỏ qua các lần gửi lặp của watchdog (250 ms) cho cùng hướng.
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
        /// `alert` có liên quan giới hạn chuyển động (soft/hard limit, align/relative bị từ chối) hay
        /// không — các cảnh báo này LUÔN đi kèm một mã lỗi trong ERROR telemetry nên không toast riêng
        /// (xem chú thích tại nhánh xử lý `alert` trong HandleIncoming).
        /// </summary>
        private static bool IsLimitAlert(string alert)
            => alert.Contains("limit", StringComparison.OrdinalIgnoreCase);

        /// <summary>Đưa một dòng trả lời "như từ thiết bị" vào pipeline (và cho adapter ISerialLink).</summary>
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
        // SYSTEM LOG (giống bảng System Log của Web UI)
        // Chỉ ghi thông báo của firmware/plugin: timestamp + tô màu theo từ khóa,
        // dòng mới nhất lên trên, tối đa 50 dòng. KHÔNG ghi frame TX/RX thô.
        // ==================================================================
        private const int SystemLogMaxEntries = 50;

        public ObservableCollection<SystemLogEntry> SystemLog { get; } = new();

        /// <summary>
        /// Ghi chẩn đoán của PLUGIN vào NINA log — KHÔNG đưa vào bảng System log.
        /// Bảng System log chỉ chứa thông báo do THIẾT BỊ gửi (giống hệt bảng System Log của Web UI);
        /// nếu thêm chữ của plugin vào đây thì nội dung sẽ khác web.
        /// </summary>
        private void AppendLog(string text)
        {
            try
            {
                Logger.Info($"[MLAstro][WS] {text}");
            }
            catch { }
        }

        /// <summary>Thêm một dòng vào System log (đã lọc nhiễu + tô màu như Web UI).</summary>
        public void AddSystemLog(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            // Web UI bỏ dòng này vì nó xuất hiện quá nhiều và không hữu ích.
            if (message.Contains("Manual stop sequence completed. Hardlimit re-enabled."))
            {
                return;
            }

            var entry = new SystemLogEntry(message, ClassifyLogLevel(message), DateTime.Now);

            void Add()
            {
                SystemLog.Insert(0, entry); // mới nhất lên trên
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

        /// <summary>Nội dung CSV của System log (cũ nhất trước) để Export CSV.</summary>
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

        /// <summary>Phân loại màu đúng theo quy tắc của Web UI (appendLog).</summary>
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
