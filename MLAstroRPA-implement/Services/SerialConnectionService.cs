using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Globalization;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using MLAstroRPA.Settings;
using NINA.Core.Utility;
 
namespace MLAstroRPA.Services
{
    [Export(typeof(SerialConnectionService))]
    [PartCreationPolicy(CreationPolicy.Shared)]
    public class SerialConnectionService : INotifyPropertyChanged, IDisposable
    { 
        private const int MaxTerminalEntries = 500;
        private const string InitialHandshakeCommand = "[MLAstroRPA-TC]\n";
        private const string ConnectionCheckCommand = "?\n";
        private const int PortOpenTimeoutMilliseconds = 3000;
        private const int ConnectionCheckFailThreshold = 3;
        public const int HandshakeTimeoutMinMilliseconds = 100;
        public const int HandshakeTimeoutMaxMilliseconds = 5000;
        private int _handshakeTimeoutMilliseconds = 300;

        public const int PollingIntervalMinMilliseconds = 100;
        public const int PollingIntervalMaxMilliseconds = 1000;
        private int _pollingIntervalMilliseconds = 300;

        // Static flag to pause query on ALL instances
        private static bool _pauseQueryGlobal;

        /// <summary>Xảy ra khi <see cref="PauseQueryGlobal"/> đổi (auto khi TPPA mượn/trả cổng, hoặc gạt tay)
        /// để UI checkbox "Pause polling '?'" luôn phản ánh đúng trạng thái thật.</summary>
        public static event Action<bool> PauseQueryChanged;

        public static bool PauseQueryGlobal
        {
            get => _pauseQueryGlobal;
            set
            {
                if (_pauseQueryGlobal == value) return;
                _pauseQueryGlobal = value;
                Logger.Info($"[MLAstro] PauseQueryGlobal set to: {value}");
                try { PauseQueryChanged?.Invoke(value); } catch { }
            }
        }

        // Static singleton instance to ensure all components use the same instance
        private static SerialConnectionService? _instance;
        private static readonly object _instanceLock = new();

        /// <summary>
        /// Gets the singleton instance of SerialConnectionService.
        /// Use this property instead of MEF injection to ensure single instance across all components.
        /// </summary>
        public static SerialConnectionService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_instanceLock)
                    {
                        _instance ??= new SerialConnectionService(PluginSettings.Instance);
                    }
                }
                return _instance!;
            }
        }

        private readonly PluginSettings _settings;
        private readonly StringBuilder _telemetryBuffer = new();
        private bool _pauseTelemetryUpdates;
        private bool _suspendSettingsSync;
        private volatile bool _applyingTelemetrySettings;
        private SerialPort _serialPort = null!;
        private string[] _availablePorts = Array.Empty<string>();
        private ComPortInfo[] _availableComPortInfos = Array.Empty<ComPortInfo>();
        private string _connectionStatus = "Disconnected";
        private string _handshakeStatus = string.Empty;
        private string _firmwareVersion = "unknown";
        private bool _hexDisplay;
        private readonly object _responseSync = new();
        private readonly SemaphoreSlim _serialOperationSemaphore = new(1, 1);
        private readonly StringBuilder _lineBuffer = new();
        private TaskCompletionSource<bool>? _pendingCommandTcs;
        private TaskCompletionSource<bool>? _anyResponseTcs;
        private System.Timers.Timer? _connectionCheckTimer;
        private int _connectionCheckInProgress;
        private int _portOpenInProgress;
        private int _connectionCheckFailures;
        private ManagementEventWatcher? _deviceChangeWatcher;

        // ===== LINK WATCHDOG (PC client) =====
        // Phiên đã handshake OK mà KHÔNG nhận được bất kỳ dữ liệu nào từ thiết bị trong
        // LinkWatchdogTimeoutMs (Serial: telemetry trả lời poll '?'; Wireless: telemetry/log đẩy
        // liên tục) => coi như MẤT LIÊN LẠC, tự Disconnect (toast + dòng System log; việc nhả
        // quyền TPPA do Disconnect() -> RaiseExternalState(false) lo sẵn).
        private const int LinkWatchdogTimeoutMs = 5000;
        private System.Timers.Timer? _linkWatchdogTimer;
        private DateTime _lastRxUtc = DateTime.UtcNow;
        private int _linkWatchdogChecking;

        public event PropertyChangedEventHandler? PropertyChanged;
        public event EventHandler<TelemetryDataEventArgs>? TelemetryDataReceived;
        public event EventHandler<string>? CompletionReceived;
        public event EventHandler<DriverErrorState>? ErrorStateChanged;

        private DriverErrorState _errorState = DriverErrorState.Clean;

        /// <summary>
        /// Latest parsed snapshot of the firmware's dedicated "ERROR:..." telemetry line.
        /// The firmware sends this line only when the error state changes (edge-triggered),
        /// so each update here represents a real transition.
        /// </summary>
        public DriverErrorState ErrorState
        {
            get => _errorState;
            private set
            {
                _errorState = value;
                OnPropertyChanged();
            }
        }

        // =====================================================================
        // Kết nối dùng chung cho plugin ngoài trong cùng process NINA (vd TPPA).
        // MLAstro là CHỦ cổng COM duy nhất; plugin khác gọi qua API này để:
        //   - EnsureExternalConnectedAsync(): mở cổng (auto-open) nếu chưa mở.
        //   - Disconnect(): đóng cổng (đóng chung cho cả 2 phía).
        //   - Send(text): ghi lệnh (dùng chung write-lock, không đứt giữa dòng).
        //   - AddExternalLineListener / AddExternalStateListener: nhận dòng RX + trạng thái.
        // (Dùng method + Action thay vì event thuần để plugin ngoài truy cập qua reflection dễ.)
        // =====================================================================
        private readonly object _txLock = new();               // khoá ghi tuần tự (MLAstro + plugin ngoài)
        private readonly object _externalLock = new();
        private readonly List<Action<string>> _externalLineListeners = new();
        private readonly List<Action<bool>> _externalStateListeners = new();
        private readonly List<Action<string>> _externalStopListeners = new();
        private readonly List<Action<bool>> _externalControlListeners = new();
        private bool _externalControlActive;    // true khi plugin ngoài (TPPA) đang GIỮ quyền điều khiển

        /// <summary>Cổng COM đang cấu hình (chủ cổng = MLAstro dùng cấu hình này).</summary>
        public string ConfiguredComPort => _settings.ComPort;

        /// <summary>Baudrate đang cấu hình.</summary>
        public int ConfiguredBaudRate => _settings.BaudRate;

        /// <summary>Đăng ký nhận mọi dòng RX hoàn chỉnh (ok / error / &lt;telemetry&gt; / ERROR: / COMPLETED...).</summary>
        public void AddExternalLineListener(Action<string> listener)
        {
            if (listener == null) return;
            lock (_externalLock)
            {
                if (!_externalLineListeners.Contains(listener)) _externalLineListeners.Add(listener);
            }
        }

        public void RemoveExternalLineListener(Action<string> listener)
        {
            if (listener == null) return;
            lock (_externalLock) { _externalLineListeners.Remove(listener); }
        }

        /// <summary>Đăng ký nhận thay đổi trạng thái mở/đóng cổng (arg = IsConnected).</summary>
        public void AddExternalStateListener(Action<bool> listener)
        {
            if (listener == null) return;
            lock (_externalLock)
            {
                if (!_externalStateListeners.Contains(listener)) _externalStateListeners.Add(listener);
            }
        }

        public void RemoveExternalStateListener(Action<bool> listener)
        {
            if (listener == null) return;
            lock (_externalLock) { _externalStateListeners.Remove(listener); }
        }

        /// <summary>Tạm dừng poll "?" của MLAstro khi plugin ngoài (TPPA) đang chủ động điều khiển.</summary>
        public void SetExternalPauseQuery(bool pause) => PauseQueryGlobal = pause;
        /// <summary>Đang có plugin ngoài (TPPA) GIỮ quyền điều khiển -&gt; MLAstro khoá UI (trừ STOP/E-STOP + CONNECTION).</summary>
        public bool IsExternalControlActive {
            get { lock (_externalLock) return _externalControlActive || (_wirelessProxy?.IsExternalControlActive == true); }
        }

        // --- Kênh STOP / trả quyền (giữa MLAstro và plugin ngoài TPPA) ---
        public void AddExternalStopListener(Action<string> listener)
        {
            if (listener == null) return;
            lock (_externalLock) { if (!_externalStopListeners.Contains(listener)) _externalStopListeners.Add(listener); }
        }

        public void RemoveExternalStopListener(Action<string> listener)
        {
            if (listener == null) return;
            lock (_externalLock) { _externalStopListeners.Remove(listener); }
        }

        /// <summary>Đăng ký nhận thay đổi "quyền điều khiển ngoài" (arg = IsExternalControlActive) để khoá/mở khoá UI.</summary>
        public void AddExternalControlListener(Action<bool> listener)
        {
            if (listener == null) return;
            lock (_externalLock) { if (!_externalControlListeners.Contains(listener)) _externalControlListeners.Add(listener); }
        }

        public void RemoveExternalControlListener(Action<bool> listener)
        {
            if (listener == null) return;
            lock (_externalLock) { _externalControlListeners.Remove(listener); }
        }

        /// <summary>
        /// Bên MLAstro nhấn STOP/FORCE-STOP giữa chừng (hoặc đang điều khiển ngoài):
        /// báo plugin ngoài (TPPA) phải DỪNG PA ngay lập tức.
        /// </summary>
        public void NotifyExternalStop(string reason)
        {
            Logger.Info($"[MLAstro] NotifyExternalStop: {reason}");
            RaiseExternalStop(reason);
            // Wireless: báo cho phiên WebSocket (driver TPPA dùng chung service này qua proxy).
            try { _wirelessProxy?.NotifyExternalStop(reason); } catch { }
        }

        /// <summary>
        /// TPPA BẮT ĐẦU giữ quyền điều khiển: đảm bảo cổng mở (auto-open cho cả MLAstro),
        /// đánh dấu đang điều khiển ngoài (MLAstro khoá UI) và tạm dừng poll "?" của MLAstro.
        /// </summary>
        public async Task<bool> BeginExternalControlAsync()
        {
            // Wireless: mở phiên WebSocket nếu chưa có rồi đánh dấu external control trên proxy.
            if (WirelessActive)
            {
                var okWireless = await _wirelessProxy!.BeginExternalControlAsync().ConfigureAwait(false);
                if (!okWireless) return false;
                lock (_externalLock) _externalControlActive = true;
                RaiseExternalControl(true);
                return true;
            }

            var ok = await EnsureExternalConnectedAsync().ConfigureAwait(false);
            if (!ok) return false;
            lock (_externalLock) _externalControlActive = true;
            RaiseExternalControl(true);
            PauseQueryGlobal = true;
            return true;
        }

        /// <summary>
        /// TPPA THẢ quyền điều khiển: KHÔNG đóng cổng - chỉ ngắt liên lạc điều khiển,
        /// MLAstro nhận lại quyền (mở khoá UI) và poll "?" trở lại.
        /// </summary>
        public void EndExternalControl()
        {
            lock (_externalLock)
            {
                if (_externalControlActive)
                {
                    _externalControlActive = false;
                    RaiseExternalControl(false);
                }
            }
            // Trả quyền trên transport wireless (nếu có) để phiên WS trở lại trạng thái monitor.
            try { _wirelessProxy?.EndExternalControl(); } catch { }
            // Luôn nhả cờ tạm dừng poll toàn cục, kể cả khi cờ active đã bị Disconnect() xoá từ trước.
            // Nếu không, PauseQueryGlobal kẹt true vĩnh viễn -> MLAstro ngừng poll "?" cho tới khi có
            // chu kỳ mượn mới hoặc gạt tay tắt pause.
            PauseQueryGlobal = false;
            Logger.Info("[MLAstro] EndExternalControl: released control to local UI (port stays open).");
        }

        /// <summary>
        /// Đảm bảo cổng đã mở (theo cấu hình MLAstro) cho plugin ngoài dùng.
        /// Nếu MLAstro chưa mở thì mở luôn -&gt; cả 2 plugin cùng báo Connected.
        /// </summary>
        public async Task<bool> EnsureExternalConnectedAsync()
        {
            if (IsConnected) return true;
            return await ConnectAsync(ConfiguredComPort, ConfiguredBaudRate).ConfigureAwait(false);
        }

        /// <summary>Ghi tuần tự qua write-lock (tránh 2 luồng ghi đè giữa dòng lệnh).</summary>
        private void WriteBytes(byte[] data)
        {
            lock (_txLock)
            {
                _serialPort?.Write(data, 0, data.Length);
            }
        }

        private void RaiseExternalLine(string line)
        {
            List<Action<string>>? copy = null;
            lock (_externalLock)
            {
                if (_externalLineListeners.Count > 0) copy = _externalLineListeners.ToList();
            }
            if (copy == null) return;
            foreach (var l in copy)
            {
                try { l(line); }
                catch { /* plugin ngoài lỗi không làm ảnh hưởng MLAstro */ }
            }
        }

        private void RaiseExternalState(bool connected)
        {
            List<Action<bool>>? copy = null;
            lock (_externalLock)
            {
                if (_externalStateListeners.Count > 0) copy = _externalStateListeners.ToList();
            }
            if (copy == null) return;
            foreach (var l in copy)
            {
                try { l(connected); }
                catch { /* bỏ qua */ }
            }
        }

        private void RaiseExternalStop(string reason)
        {
            List<Action<string>>? copy = null;
            lock (_externalLock)
            {
                if (_externalStopListeners.Count > 0) copy = _externalStopListeners.ToList();
            }
            if (copy == null) return;
            foreach (var l in copy)
            {
                try { l(reason); }
                catch { /* bỏ qua */ }
            }
        }

        private void RaiseExternalControl(bool active)
        {
            List<Action<bool>>? copy = null;
            lock (_externalLock)
            {
                if (_externalControlListeners.Count > 0) copy = _externalControlListeners.ToList();
            }
            if (copy == null) return;
            foreach (var l in copy)
            {
                try { l(active); }
                catch { /* bỏ qua */ }
            }
        }

        [ImportingConstructor]
        public SerialConnectionService(PluginSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));

            // Load persisted timing settings (clamped to valid ranges)
            _handshakeTimeoutMilliseconds = Math.Clamp(_settings.HandshakeTimeoutMilliseconds, HandshakeTimeoutMinMilliseconds, HandshakeTimeoutMaxMilliseconds);
            _pollingIntervalMilliseconds = Math.Clamp(_settings.PollingIntervalMilliseconds, PollingIntervalMinMilliseconds, PollingIntervalMaxMilliseconds);

            Logger.Info("[MLAstro] SerialConnectionService created");

            // Register as singleton if not already set
            lock (_instanceLock)
            {
                _instance ??= this;
            }
        }

        public ObservableCollection<SerialTerminalEntry> TerminalEntries { get; } = new();

        public string[] AvailablePorts
        {
            get => _availablePorts;
            private set
            {
                _availablePorts = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// COM ports with their driver-friendly display names (e.g. "COM4 - USB-SERIAL CH340").
        /// <see cref="ComPortInfo.PortName"/> keeps the raw name used for connecting.
        /// </summary>
        public ComPortInfo[] AvailableComPortInfos
        {
            get => _availableComPortInfos;
            private set
            {
                _availableComPortInfos = value;
                OnPropertyChanged();
            }
        }

        public bool IsConnected => _serialPort?.IsOpen == true || WirelessActive;

        /// <summary>
        /// Đường đang dùng để nói chuyện với thiết bị (dùng cho HeaderBar):
        ///   "AP"  = wireless, firmware báo phiên WS này vào qua hotspot của ESP32;
        ///   "STA" = wireless, đi qua router mà ESP32 đã join;
        ///   "COM" = cáp USB/Serial.
        /// Rỗng khi chưa kết nối.
        /// </summary>
        public string LinkPath => WirelessActive
            ? _wirelessProxy!.LinkPath
            : (_serialPort?.IsOpen == true ? "COM" : string.Empty);

        // ==================================================================
        // FACADE cho transport WIRELESS (WebSocket)
        // Khi có proxy đang kết nối, service này đóng vai "cổng vào duy nhất" cho UI/dock/controller:
        // trạng thái, gửi lệnh và external-control đều đi qua WebSocket, còn cổng COM không mở.
        // (Mỗi lúc chỉ 1 transport hoạt động - do người dùng chọn ở tab CONNECTION.)
        // ==================================================================
        private MlastroWebSocketService? _wirelessProxy;

        public MlastroWebSocketService? WirelessProxy
        {
            get => _wirelessProxy;
            set
            {
                if (ReferenceEquals(_wirelessProxy, value))
                {
                    return;
                }

                if (_wirelessProxy != null)
                {
                    _wirelessProxy.PropertyChanged -= OnWirelessProxyPropertyChanged;
                }

                _wirelessProxy = value;

                if (_wirelessProxy != null)
                {
                    _wirelessProxy.PropertyChanged += OnWirelessProxyPropertyChanged;
                }

                OnPropertyChanged(nameof(IsConnected));
                OnPropertyChanged(nameof(ConnectionStatus));
                OnPropertyChanged(nameof(HandshakeStatus));
            }
        }

        private bool WirelessActive => _wirelessProxy?.IsConnected == true;

        private void OnWirelessProxyPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // Cho dock/controller thấy đúng trạng thái kết nối khi đang dùng transport wireless.
            OnPropertyChanged(nameof(IsConnected));
            OnPropertyChanged(nameof(ConnectionStatus));
            OnPropertyChanged(nameof(HandshakeStatus));
            OnPropertyChanged(nameof(FirmwareVersion));
            OnPropertyChanged(nameof(LinkPath));
            InvokeOnUiThread(() => RaiseExternalState(IsConnected));

            // Phiên wireless vừa kết nối: bật watchdog + đánh dấu "còn sống" từ frame đầu tiên, để
            // trường hợp "kết nối nhưng thiết bị im lặng" cũng bị ngắt sau LinkWatchdogTimeoutMs.
            if (_wirelessProxy?.IsConnected == true)
            {
                TouchLinkAlive();
            }
        }

        /// <summary>Transport wireless: cập nhật firmware version nhận được từ handshake/init snapshot.</summary>
        public void SetWirelessFirmwareVersion(string version)
        {
            if (string.IsNullOrWhiteSpace(version))
            {
                return;
            }

            FirmwareVersion = version;
        }

        public bool HexDisplay
        {
            get => _hexDisplay;
            set
            {
                if (_hexDisplay == value)
                {
                    return;
                }

                _hexDisplay = value;
                RefreshTerminalDisplay();
                OnPropertyChanged();
            }
        }

        public int HandshakeTimeoutMilliseconds
        {
            get => _handshakeTimeoutMilliseconds;
            set
            {
                var clamped = Math.Clamp(value, HandshakeTimeoutMinMilliseconds, HandshakeTimeoutMaxMilliseconds);
                if (_handshakeTimeoutMilliseconds == clamped)
                {
                    return;
                }

                _handshakeTimeoutMilliseconds = clamped;
                _settings.HandshakeTimeoutMilliseconds = clamped;
                OnPropertyChanged();
                Logger.Info($"[MLAstro] Handshake timeout set to {clamped} ms");
            }
        }

        public int PollingIntervalMilliseconds
        {
            get => _pollingIntervalMilliseconds;
            set
            {
                var clamped = Math.Clamp(value, PollingIntervalMinMilliseconds, PollingIntervalMaxMilliseconds);
                if (_pollingIntervalMilliseconds == clamped)
                {
                    return;
                }

                _pollingIntervalMilliseconds = clamped;
                _settings.PollingIntervalMilliseconds = clamped;
                OnPropertyChanged();

                // Apply the new interval to the running poll timer if it exists
                if (_connectionCheckTimer != null)
                {
                    _connectionCheckTimer.Interval = clamped;
                }

                Logger.Info($"[MLAstro] Polling interval set to {clamped} ms");
            }
        }

        public string ConnectionStatus
        {
            get => WirelessActive ? _wirelessProxy!.ConnectionStatus : _connectionStatus;
            private set
            {
                _connectionStatus = value;
                OnPropertyChanged();
            }
        }

        public string HandshakeStatus
        {
            get => WirelessActive ? _wirelessProxy!.HandshakeStatus : _handshakeStatus;
            private set
            {
                if (_handshakeStatus == value)
                {
                    return;
                }

                _handshakeStatus = value;
                OnPropertyChanged();
            }
        }

        public string FirmwareVersion
        {
            get => _firmwareVersion;
            private set
            {
                if (_firmwareVersion == value)
                {
                    return;
                }

                _firmwareVersion = value;
                OnPropertyChanged();
            }
        }

        public bool PauseTelemetryUpdates
        {
            get => _pauseTelemetryUpdates;
            set
            {
                _pauseTelemetryUpdates = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// When true, telemetry will not write parsed settings back to <see cref="PluginSettings"/>.
        /// Used while the user is editing configuration fields so polling does not overwrite their input.
        /// </summary>
        public bool SuspendSettingsSync
        {
            get => _suspendSettingsSync;
            set
            {
                _suspendSettingsSync = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// True while settings are being written programmatically from parsed telemetry.
        /// Allows callers to distinguish device-driven updates from user edits.
        /// </summary>
        public bool IsApplyingTelemetrySettings => _applyingTelemetrySettings;

        public void RefreshPorts()
        {
            // GetPortNames() can return the same COMx several times (e.g. multiple virtual
            // HHD ports all mapped to COM1 in HKLM\HARDWARE\DEVICEMAP\SERIALCOMM), so
            // de-duplicate before ordering.
            var portNames = SerialPort.GetPortNames()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            // Keep the SAME array instances when nothing changed. Replacing the ComboBox
            // ItemsSource with fresh objects makes WPF drop the current selection and push
            // null back into Settings.ComPort (TwoWay SelectedValue) - that is why the COM
            // port appears to deselect right after choosing it and clicking Connect.
            if (portNames.SequenceEqual(_availablePorts, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            AvailablePorts = portNames;

            // Build display names (driver name + COM number) from the Windows device tree.
            var friendlyNames = GetComPortFriendlyNames();
            AvailableComPortInfos = portNames
                .Select(p => new ComPortInfo(p, friendlyNames.TryGetValue(p, out var fn) ? fn : null))
                .ToArray();
        }

        /// <summary>
        /// Maps each enumerated COM port to its driver friendly name by walking the Windows
        /// device tree (HKLM\SYSTEM\CurrentControlSet\Enum). E.g. "COM4" -> "Silicon Labs CP210x USB to UART Bridge (COM4)".
        /// </summary>
        private static Dictionary<string, string> GetComPortFriendlyNames()
        {
            var best = new Dictionary<string, PortFriendlyCandidate>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var rootKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum");
                if (rootKey == null)
                {
                    return ToFriendlyNames(best);
                }

                foreach (var busName in rootKey.GetSubKeyNames())
                {
                    using var busKey = rootKey.OpenSubKey(busName);
                    if (busKey == null)
                    {
                        continue;
                    }

                    foreach (var deviceName in busKey.GetSubKeyNames())
                    {
                        using var deviceKey = busKey.OpenSubKey(deviceName);
                        if (deviceKey == null)
                        {
                            continue;
                        }

                        WalkDeviceSubtree(deviceKey, best);
                    }
                }
            }
            catch
            {
                // Registry access can fail on some systems; return what was collected.
            }

            return ToFriendlyNames(best);
        }

        private static Dictionary<string, string> ToFriendlyNames(Dictionary<string, PortFriendlyCandidate> best)
            => best.Where(kv => !string.IsNullOrWhiteSpace(kv.Value.FriendlyName))
                   .ToDictionary(kv => kv.Key, kv => kv.Value.FriendlyName);

        private static void WalkDeviceSubtree(RegistryKey key, Dictionary<string, PortFriendlyCandidate> best)
        {
            try
            {
                CollectComPortFromKey(key, best);

                // Some Enum keys have restricted ACLs and throw on enumeration (SecurityException);
                // skip those keys and keep walking the rest so friendly names are still resolved.
                foreach (var subName in key.GetSubKeyNames())
                {
                    using var subKey = key.OpenSubKey(subName);
                    if (subKey != null)
                    {
                        WalkDeviceSubtree(subKey, best);
                    }
                }
            }
            catch
            {
                // Ignore restricted/inaccessible keys and continue with other branches.
            }
        }

        /// <summary>
        /// Collects a COM port candidate from one registry key. Candidates are scored so the
        /// most specific entry wins: a key whose "Device Parameters\PortName" AND friendly name
        /// both mention the same port is preferred, and virtual/composite devices (Bluetooth,
        /// virtual network ports, ...) are deprioritized so the real USB-serial adapter wins.
        /// </summary>
        private static void CollectComPortFromKey(RegistryKey key, Dictionary<string, PortFriendlyCandidate> best)
        {
            try
            {
                string? portName = null;
                using (var deviceParams = key.OpenSubKey("Device Parameters"))
                {
                    portName = deviceParams?.GetValue("PortName") as string;
                }

                var friendly = key.GetValue("FriendlyName") as string;
                if (string.IsNullOrWhiteSpace(friendly))
                {
                    return;
                }

                var normalizedPort = NormalizePortName(portName);
                var portMatch = !string.IsNullOrWhiteSpace(normalizedPort)
                                && Regex.IsMatch(normalizedPort, @"^COM\d+$", RegexOptions.IgnoreCase);

                // Port mentioned in the friendly name, e.g. "... USB to UART Bridge (COM4)".
                var match = Regex.Match(friendly, @"\(COM\d+\)", RegexOptions.IgnoreCase);
                var friendlyPort = match.Success ? NormalizePortName(match.Value.Trim('(', ')')) : null;
                var friendlyPortMatch = portMatch
                                        && string.Equals(normalizedPort, friendlyPort, StringComparison.OrdinalIgnoreCase);

                var isVirtual = Regex.IsMatch(friendly, @"Bluetooth|Network Serial|Virtual|Emulator", RegexOptions.IgnoreCase);

                if (portMatch)
                {
                    var score = 2 + (friendlyPortMatch ? 2 : 0) + (isVirtual ? 0 : 1);
                    UpdateBest(best, normalizedPort!, score, friendly);
                }

                if (!string.IsNullOrWhiteSpace(friendlyPort))
                {
                    var score = 1 + (friendlyPortMatch ? 2 : 0) + (isVirtual ? 0 : 1);
                    UpdateBest(best, friendlyPort!, score, friendly);
                }
            }
            catch
            {
                // Ignore failures for individual device keys.
            }
        }

        private static void UpdateBest(Dictionary<string, PortFriendlyCandidate> best, string portName, int score, string friendly)
        {
            if (best.TryGetValue(portName, out var existing) && existing.Score >= score)
            {
                return;
            }

            best[portName] = new PortFriendlyCandidate(score, friendly);
        }

        private static string? NormalizePortName(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var trimmed = value.Trim();
            if (trimmed.StartsWith(@"\\.\", StringComparison.Ordinal))
            {
                trimmed = trimmed.Substring(4);
            }

            return trimmed;
        }

        private readonly struct PortFriendlyCandidate
        {
            public PortFriendlyCandidate(int score, string friendlyName)
            {
                Score = score;
                FriendlyName = friendlyName;
            }

            public int Score { get; }
            public string FriendlyName { get; }
        }

        public async Task<bool> ConnectAsync(string portName, int baudRate)
        {
            if (string.IsNullOrWhiteSpace(portName))
            {
                ConnectionStatus = "No COM port selected";
                return false;
            }

            if (Interlocked.CompareExchange(ref _portOpenInProgress, 1, 0) != 0)
            {
                ConnectionStatus = "Previous COM port open is still finishing";
                return false;
            }

            SerialPort openingPort = null!;
            var cleanupAfterTimeout = false;
            try
            {
                Disconnect();

                openingPort = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
                {
                    Handshake = Handshake.None,
                    ReadTimeout = 1000,
                    WriteTimeout = 1000,
                    Encoding = Encoding.UTF8
                };
                openingPort.DataReceived += OnSerialPortDataReceived;

                var openTask = Task.Run(openingPort.Open);
                if (await Task.WhenAny(openTask, Task.Delay(PortOpenTimeoutMilliseconds)).ConfigureAwait(false) != openTask)
                {
                    var timedOutPort = openingPort;
                    openingPort = null!;
                    cleanupAfterTimeout = true;
                    // Release the in-progress flag immediately so the user can try another port.
                    // The abandoned openTask may still be hanging; the continuation below only cleans up.
                    Interlocked.Exchange(ref _portOpenInProgress, 0);
                    ConnectionStatus = $"Connect timed out after {PortOpenTimeoutMilliseconds} ms: {portName}";
                    Logger.Warning($"[MLAstro] Serial connect timed out: {portName} @ {baudRate}");

                    _ = openTask.ContinueWith(task =>
                    {
                        try
                        {
                            timedOutPort.DataReceived -= OnSerialPortDataReceived;
                            if (timedOutPort.IsOpen)
                            {
                                timedOutPort.Close();
                            }
                            timedOutPort.Dispose();
                            if (task.IsFaulted)
                            {
                                Logger.Warning($"[MLAstro] Timed-out serial open failed: {task.Exception?.GetBaseException().Message}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Warning($"[MLAstro] Timed-out serial port cleanup failed: {ex.Message}");
                        }
                    }, TaskScheduler.Default);

                    return false;
                }

                await openTask.ConfigureAwait(false);
                _serialPort = openingPort;
                openingPort = null!;

                ConnectionStatus = $"Connected: {portName} @ {baudRate} (8-N-1)";
                HandshakeStatus = string.Empty;
                _connectionCheckFailures = 0;
                AppendTerminalEntry(SerialTerminalEntry.Connected(ConnectionStatus));
                OnPropertyChanged(nameof(IsConnected));
                Logger.Info($"[MLAstro] Serial connected: {portName} @ {baudRate} (8-N-1)");
                // Đồng bộ cổng đang kết nối vào cài đặt để dropdown "COM Port" trên CONNECTION tab tự
                // chọn đúng cổng vừa mở (kể cả khi cổng do TPPA auto-detect tìm ra & mở qua chủ MLAstro).
                _settings.ComPort = portName;
                // Mở cổng mới thành công: nhả cờ tạm dừng poll toàn cục (phòng khi kẹt từ phiên TPPA mượn
                // trước đó) để MLAstro poll "?" lại ngay từ đầu.
                PauseQueryGlobal = false;
                StartConnectionCheckTimer();
                StartDeviceChangeWatcher();
                _ = StartHandshakeAndConnectionChecksAsync();
                RaiseExternalState(true);
                return true;
            }
            catch (Exception ex)
            {
                ConnectionStatus = $"Connect failed: {ex.Message}";
                Logger.Warning($"[MLAstro] Serial connect failed: {ex.Message}");
                DisconnectPortInstance();
                OnPropertyChanged(nameof(IsConnected));
                return false;
            }
            finally
            {
                if (openingPort != null)
                {
                    openingPort.DataReceived -= OnSerialPortDataReceived;
                    openingPort.Dispose();
                }

                if (!cleanupAfterTimeout)
                {
                    Interlocked.Exchange(ref _portOpenInProgress, 0);
                }
            }
        }

        public bool SendHex(string hexText)
        {
            if (!IsConnected)
            {
                ConnectionStatus = "Not connected";
                return false;
            }

            if (string.IsNullOrWhiteSpace(hexText))
            {
                return false;
            }

            var normalizedHex = new string(hexText.Where(Uri.IsHexDigit).ToArray());
            if (normalizedHex.Length == 0)
            {
                return false;
            }

            if (normalizedHex.Length % 2 != 0)
            {
                ConnectionStatus = "Hex input must contain an even number of digits";
                return false;
            }

            try
            {
                var data = Enumerable.Range(0, normalizedHex.Length / 2)
                    .Select(i => Convert.ToByte(normalizedHex.Substring(i * 2, 2), 16))
                    .ToArray();

                WriteBytes(data);
                AppendTerminalEntry(SerialTerminalEntry.Sent(data, _serialPort.Encoding, HexDisplay));
                return true;
            }
            catch (Exception ex)
            {
                ConnectionStatus = $"Hex send failed: {ex.Message}";
                Logger.Warning($"[MLAstro] Serial hex send failed: {ex.Message}");
                return false;
            }
        }

        public void Disconnect()
        {
            // Transport wireless đang hoạt động → nhả quyền + đóng WebSocket (không đụng cổng COM).
            if (WirelessActive)
            {
                try { _wirelessProxy!.Disconnect(); } catch { }
                ConnectionStatus = "Disconnected";
                HandshakeStatus = string.Empty;
                AppendTerminalEntry(SerialTerminalEntry.Disconnected("Disconnected: wireless"));
                OnPropertyChanged(nameof(IsConnected));
                RaiseExternalState(false);
                Logger.Info("[MLAstro] Wireless disconnected (proxy).");
                return;
            }

            _connectionCheckFailures = 0;

            // Nếu có plugin ngoài (TPPA) đang GIỮ quyền: báo dừng PA + trả quyền về UI trước khi đóng cổng.
            lock (_externalLock)
            {
                if (_externalControlActive)
                {
                    _externalControlActive = false;
                    RaiseExternalControl(false);
                    RaiseExternalStop("MLAstro disconnected");
                }
            }
            // MLAstro đóng cổng (có thể đang giữa phiên TPPA mượn): NHẢ cờ tạm dừng poll toàn cục để
            // lần kết nối sau poll "?" bình thường (không bị kẹt bởi phiên mượn trước).
            PauseQueryGlobal = false;

            if (_serialPort == null)
            {
                ConnectionStatus = "Disconnected";
                HandshakeStatus = string.Empty;
                OnPropertyChanged(nameof(IsConnected));
                return;
            }

            // Capture trước khi gọi bất kỳ method nào (tránh CS8602 do trình biên dịch reset null-state của field).
            var portName = _serialPort.PortName;

            // Best-effort: gửi lệnh "Disconnect\n" cho firmware NGAY TRƯỚC khi đóng cổng, để thiết bị
            // nhả handshake chủ động. Quan trọng khi Communication Watchdog TẮT (firmware không tự
            // nhả handshake) — nếu không gửi, thiết bị sẽ giữ trạng thái "Serial control" vô thời hạn.
            if (_serialPort?.IsOpen == true)
            {
                try
                {
                    Send("Disconnect\n");
                }
                catch (Exception ex)
                {
                    Logger.Warning($"[MLAstro] Failed to send Disconnect command: {ex.Message}");
                }
            }

            DisconnectPortInstance();
            ConnectionStatus = "Disconnected";
            HandshakeStatus = string.Empty;
            AppendTerminalEntry(SerialTerminalEntry.Disconnected($"Disconnected: {portName}"));
            OnPropertyChanged(nameof(IsConnected));
            RaiseExternalState(false);
            Logger.Info($"[MLAstro] Serial disconnected: {portName}");
        }

        public bool ResetEsp32()
        {
            if (WirelessActive)
            {
                return _wirelessProxy!.ResetEsp32();
            }

            if (_serialPort?.IsOpen != true)
            {
                ConnectionStatus = "Not connected";
                return false;
            }

            try
            {
                // Standard ESP32 auto-reset sequence over DTR/RTS:
                // DTR=false keeps GPIO0 high (normal boot), RTS toggles EN (reset).
                _serialPort.DtrEnable = false;
                _serialPort.RtsEnable = true;
                Thread.Sleep(100);
                _serialPort.RtsEnable = false;
                _serialPort.DtrEnable = false;

                ConnectionStatus = "ESP32 reset";
                AppendTerminalEntry(SerialTerminalEntry.Disconnected("ESP32 reset via serial"));
                Logger.Info("[MLAstro] ESP32 reset via DTR/RTS");
                return true;
            }
            catch (Exception ex)
            {
                ConnectionStatus = $"ESP32 reset failed: {ex.Message}";
                Logger.Warning($"[MLAstro] ESP32 reset failed: {ex.Message}");
                return false;
            }
        }

        public bool Send(string text)
        {
            // Transport wireless: dịch lệnh text sang JSON và gửi qua WebSocket.
            if (WirelessActive)
            {
                return _wirelessProxy!.Send(text);
            }

            if (!IsConnected)
            {
                ConnectionStatus = "Not connected";
                return false;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            try
            {
                var data = _serialPort.Encoding.GetBytes(text);
                WriteBytes(data);
                AppendTerminalEntry(SerialTerminalEntry.Sent(data, _serialPort.Encoding, HexDisplay));

                // Log sent commands (exclude telemetry query for cleaner logs)
                if (!text.Equals("?\n"))
                {
                    Logger.Info($"[MLAstro] Command sent: {text.TrimEnd('\r', '\n')}");
                }

                return true;
            }
            catch (Exception ex)
            {
                ConnectionStatus = $"Send failed: {ex.Message}";
                Logger.Warning($"[MLAstro] Serial send failed: {ex.Message}");
                return false;
            }
        }

        public void ClearTerminal()
        {
            InvokeOnUiThread(() => TerminalEntries.Clear());
        }

        public bool QueryTelemetry()
        {
            if (PauseQueryGlobal)
            {
                return false;
            }

            // Wireless: firmware tự đẩy telemetry ~250 ms, không cần poll "?".
            if (WirelessActive)
            {
                return true;
            }

            return Send("?\n");
        }

        public bool QueryApPassword()
        {
            if (!IsConnected)
            {
                return false;
            }

            return Send("APpa:?\n");
        }

        public bool QueryStaPassword()
        {
            if (!IsConnected)
            {
                return false;
            }

            return Send("STAp:?\n");
        }

        public string BuildConfigurationCommand(PluginSettings settings, bool includeSaveAndReboot = true)
        {
            if (settings == null)
            {
                return string.Empty;
            }

            var parts = new System.Collections.Generic.List<string>();

            // Soft Limits
            parts.Add($"AzL1:{settings.LimitAzMin.ToString(CultureInfo.InvariantCulture)}");
            parts.Add($"AzL2:{settings.LimitAzMax.ToString(CultureInfo.InvariantCulture)}");
            parts.Add($"AlL1:{settings.LimitAltMin.ToString(CultureInfo.InvariantCulture)}");
            parts.Add($"AlL2:{settings.LimitAltMax.ToString(CultureInfo.InvariantCulture)}");

            // Azimuth Motor
            parts.Add($"AzRD:{(settings.AzReverse ? 1 : 0)}");
            parts.Add($"AzIR:{settings.AzCurrentRun}");
            parts.Add($"AzIH:{settings.AzCurrentHold}");
            parts.Add($"AzSB:{settings.AzBooster}");
            parts.Add($"AzSC:{settings.AzCoolStep}");
            parts.Add($"AzMS:{settings.AzMicrosteps}");
            parts.Add($"AzAc:{settings.AzAccel}");
            parts.Add($"AzDec:{settings.AzDecel}");
            parts.Add($"AzSD:{settings.AzStepsPerDegree.ToString(CultureInfo.InvariantCulture)}");
            parts.Add($"AzRM:{settings.AzMode}");

            // Altitude Motor
            parts.Add($"AlRD:{(settings.AltReverse ? 1 : 0)}");
            parts.Add($"AlIR:{settings.AltCurrentRun}");
            parts.Add($"AlIH:{settings.AltCurrentHold}");
            parts.Add($"AlSB:{settings.AltBooster}");
            parts.Add($"AlSC:{settings.AltCoolStep}");
            parts.Add($"AlMS:{settings.AltMicrosteps}");
            parts.Add($"AlAc:{settings.AltAccel}");
            parts.Add($"AlDe:{settings.AltDecel}");
            parts.Add($"AlSD:{settings.AltStepsPerDegree.ToString(CultureInfo.InvariantCulture)}");
            parts.Add($"AlRM:{settings.AltMode}");

            // Backlash
            parts.Add($"Back:{(settings.BacklashEnabled ? 1 : 0)}");
            parts.Add($"AzBl:{settings.BacklashAz}");
            parts.Add($"AlBl:{settings.BacklashAlt}");

            // P.A Overshoot
            parts.Add($"Over:{(settings.OvershootEnabled ? 1 : 0)}");
            parts.Add($"OvUp:{(settings.OvershootMoveUp ? 1 : 0)}");
            parts.Add($"OvDn:{(settings.OvershootMoveDown ? 1 : 0)}");
            parts.Add($"OvD:{settings.OvershootDegrees}");
            parts.Add($"OvM:{settings.OvershootMinutes}");
            parts.Add($"OvS:{settings.OvershootSeconds}");

            // WiFi Settings
            if (!string.IsNullOrWhiteSpace(settings.ApSsid))
                parts.Add($"APss:{settings.ApSsid}");
            if (!string.IsNullOrWhiteSpace(settings.ApIp))
                parts.Add($"APip:{settings.ApIp}");
            if (!string.IsNullOrWhiteSpace(settings.WifiSsid))
                parts.Add($"STAs:{settings.WifiSsid}");

            // Add Save&Reboot command at the end when persisting settings
            if (includeSaveAndReboot)
            {
                parts.Add("Save&Reboot:1");
            }

            return string.Join(",", parts) + "\n";
        }

        private void OnSerialPortDataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                if (_serialPort == null)
                {
                    return;
                }

                var bytesToRead = _serialPort.BytesToRead;
                if (bytesToRead <= 0)
                {
                    return;
                }

                var buffer = new byte[bytesToRead];
                var bytesRead = _serialPort.Read(buffer, 0, bytesToRead);
                if (bytesRead <= 0)
                {
                    return;
                }

                if (bytesRead != buffer.Length)
                {
                    Array.Resize(ref buffer, bytesRead);
                }

                TouchLinkAlive();   // nuôi watchdog: vừa có dữ liệu từ thiết bị qua COM
                AppendTerminalEntry(SerialTerminalEntry.Received(buffer, _serialPort.Encoding, HexDisplay));
                var receivedText = _serialPort.Encoding.GetString(buffer);

                // Frame the incoming stream by newline and classify each complete line into:
                //  1) Telemetry (<...), 2) pending command answer (ok/error), 3) spontaneous firmware responses.
                _lineBuffer.Append(receivedText);
                var buf = _lineBuffer.ToString();
                int nl;
                while ((nl = buf.IndexOf('\n')) >= 0)
                {
                    var line = buf.Substring(0, nl).TrimEnd('\r');
                    buf = buf.Substring(nl + 1);
                    if (line.Length > 0)
                    {
                        RouteLine(line);
                        RaiseExternalLine(line);
                    }
                }
                _lineBuffer.Clear();
                _lineBuffer.Append(buf);
            }
            catch (Exception ex)
            {
                Logger.Warning($"[MLAstro] Serial receive failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Routes a complete, newline-terminated line into one of 3 buckets:
        ///  1) Telemetry (starts with '&lt;'),
        ///  2) Answer to a pending command ('ok' / 'error...'),
        ///  3) Spontaneous firmware response (COMPLETED, DISCONNECTED, APpa:/STAp:, etc.).
        /// </summary>
        private void RouteLine(string line)
        {
            // Bucket 1: Telemetry
            if (line.StartsWith("<"))
            {
                LogReceivedLine(line);
                ProcessTelemetryData(line);
                TryUpdateDeviceInfo(line);
                CheckForCompletionEvents(line);
                SignalAnyResponse();
                return;
            }

            // Bucket 1b: Error telemetry - the firmware's dedicated "ERROR:..." line (uppercase).
            // MUST be checked BEFORE Bucket 2: the case-insensitive "error" match below would
            // otherwise treat this telemetry as a failed command reply, dropping the error codes
            // and falsely failing whatever command is pending.
            if (line.StartsWith("ERROR:", StringComparison.Ordinal))
            {
                LogReceivedLine(line);
                ProcessErrorTelemetry(line);
                SignalAnyResponse();
                return;
            }

            // Bucket 1c: marker "All Setting Saved" — firmware xác nhận ĐÃ ghi FRAM xong (Save&Reboot:1).
            // Đây là tín hiệu DUY NHẤT để plugin biết đã lưu thành công và được phép reset ESP qua EN pin.
            if (line.IndexOf(AllSettingsSavedMarker, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                LogReceivedLine(line);
                _allSettingsSavedTcs?.TrySetResult(true);
            }

            // Bucket 2: Answer to a pending command
            if (line.StartsWith("ok", StringComparison.OrdinalIgnoreCase))
            {
                LogReceivedLine(line);
                TryUpdateDeviceInfo(line); // handshake: "ok,firmware...,SN:.."
                ResolvePendingCommand(success: true);
                SignalAnyResponse();
                return;
            }
            if (line.StartsWith("error", StringComparison.OrdinalIgnoreCase))
            {
                LogReceivedLine(line);
                ResolvePendingCommand(success: false);
                SignalAnyResponse();
                return;
            }

            // Bucket 3: Spontaneous firmware response
            LogReceivedLine(line);
            ProcessWifiPasswordResponses(line);
            CheckForCompletionEvents(line);
            TryUpdateDeviceInfo(line);
            HandleDisconnected(line);
            SignalAnyResponse();
        }

        private void LogReceivedLine(string line)
        {
            if (line.StartsWith("<"))
            {
                Logger.Info($"[MLAstro] Telemetry received: {line.Substring(0, Math.Min(200, line.Length))}...");
            }
            else if (line.StartsWith("ERROR:", StringComparison.Ordinal))
            {
                // Dedicated error telemetry (not a command reply) - log as warning with the summary
                Logger.Warning($"[MLAstro] Error telemetry: {line}");
            }
            else if (line.StartsWith("ok", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Info("[MLAstro] OK response received");
            }
            else if (line.StartsWith("error", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Warning($"[MLAstro] Error response: {line}");
            }
            else if (line.Contains("COMPLETED"))
            {
                Logger.Info($"[MLAstro] Completion event: {line}");
            }
            else if (line.Contains("DISCONNECTED"))
            {
                Logger.Info("[MLAstro] DISCONNECTED signal received from device");
            }
        }

        private void HandleDisconnected(string line)
        {
            if (line.Contains("DISCONNECTED"))
            {
                Logger.Info("[MLAstro] DISCONNECTED signal received from device - auto disconnecting");
                Application.Current?.Dispatcher?.BeginInvoke(new Action(Disconnect));
            }
        }

        private void ResolvePendingCommand(bool success)
        {
            lock (_responseSync)
            {
                _pendingCommandTcs?.TrySetResult(success);
                _pendingCommandTcs = null;
            }
        }

        private void SignalAnyResponse()
        {
            lock (_responseSync) _anyResponseTcs?.TrySetResult(true);
        }

        /// <summary>
        /// Parses the firmware's dedicated error telemetry line, e.g.
        /// "ERROR:Sys:0,AzNC:2,AlNC:0,...Esc:0". Values are 0 = OK, 1 = WARNING, 2 = ERROR.
        /// Raises <see cref="ErrorStateChanged"/> on every line (the firmware is edge-triggered).
        /// </summary>
        private void ProcessErrorTelemetry(string line)
        {
            // BẮT BUỘC đúng dòng ERROR telemetry của firmware ("ERROR:Code:value,Code:value,...").
            // Hàm này còn được gọi từ InjectIncomingText (transport wireless); nếu không kiểm tra prefix
            // thì dòng telemetry thường dạng <STATUS|Mpos:x,y|>WSta:1,Home:1,AzRM:1,... sẽ bị parse
            // thành mã lỗi và sinh ra các WARNING giả trong Alarm History (WSta/Home/AzRM/AlRM/Back).
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("ERROR:", StringComparison.Ordinal))
            {
                return;
            }

            try
            {
                var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var body = line.StartsWith("ERROR:", StringComparison.Ordinal) ? line.Substring("ERROR:".Length) : line;
                foreach (var token in body.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var idx = token.IndexOf(':');
                    if (idx <= 0)
                    {
                        continue;
                    }

                    var key = token.Substring(0, idx).Trim();
                    var valueText = token.Substring(idx + 1).Trim();
                    if (int.TryParse(valueText, out var value))
                    {
                        dict[key] = value;
                    }
                }

                // `CmdRf:N` = bitfield các LỆNH BỊ TỪ CHỐI vì soft-limit (mỗi bit 1 loại lệnh; bit
                // BẬT ngay khi lệnh bị từ chối, firmware tự TẮT sau ~1.5 s nếu loại lệnh đó không còn
                // bị từ chối nữa). Bung thành từng mã riêng với giá trị 1 = WARNING để bảng Alarm
                // hiển thị mỗi loại lệnh một dòng có tên rõ ràng (thay vì dòng "CmdRf: 5" vô nghĩa).
                if (dict.Remove("CmdRf", out var refusedBits))
                {
                    if ((refusedBits & 0x01) != 0) dict["RfRelAz"] = 1;   // relative move AZ vượt soft-limit
                    if ((refusedBits & 0x02) != 0) dict["RfRelAl"] = 1;   // relative move ALT vượt soft-limit
                    if ((refusedBits & 0x04) != 0) dict["RfAlnAz"] = 1;   // align: target AZ ngoài giới hạn
                    if ((refusedBits & 0x08) != 0) dict["RfAlnAl"] = 1;   // align: target ALT ngoài giới hạn
                    if ((refusedBits & 0x10) != 0) dict["RfJogAz"] = 1;   // jog AZ tại giới hạn
                    if ((refusedBits & 0x20) != 0) dict["RfJogAl"] = 1;   // jog ALT tại giới hạn
                    if ((refusedBits & 0x40) != 0) dict["RfAlnOv"] = 1;   // align nhánh overshoot (ALT)
                }

                var state = new DriverErrorState(dict);
                Logger.Info($"[MLAstro] Error telemetry parsed: {(state.HasErrors || state.HasWarnings ? state.Summary : "All clear")}");
                ErrorState = state;
                InvokeOnUiThread(() => ErrorStateChanged?.Invoke(this, state));
            }
            catch (Exception ex)
            {
                Logger.Warning($"[MLAstro] Error telemetry parse failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Resets the error state to clean (used on disconnect so stale errors do not survive a reconnect).
        /// </summary>
        private void ResetErrorState()
        {
            if (ErrorState.IsClean)
            {
                return;
            }

            ErrorState = DriverErrorState.Clean;
            InvokeOnUiThread(() => ErrorStateChanged?.Invoke(this, ErrorState));
        }

        /// <summary>
        /// Xoá trạng thái lỗi cũ khi bắt đầu phiên mới (transport wireless không nhận được dòng
        /// ERROR: của firmware như đường serial, nên cần chủ động đưa về trạng thái sạch).
        /// </summary>
        public void ResetErrorStateForNewSession()
        {
            ErrorState = DriverErrorState.Clean;
            InvokeOnUiThread(() => ErrorStateChanged?.Invoke(this, ErrorState));
        }

        /// <summary>
        /// Sends the handshake sequence and waits for the expected response.
        /// Used by auto-reconnect so the handshake can be retried until the firmware is ready.
        /// </summary>
        public async Task<bool> SendHandshakeAsync()
        {
            // Wireless: handshake (MLAstroRPA-TC) đã được thực hiện bằng JSON ngay khi kết nối WebSocket.
            if (WirelessActive)
            {
                return _wirelessProxy!.HandshakeStatus == "OK!";
            }

            return await SendAndAwaitOkAsync(InitialHandshakeCommand).ConfigureAwait(false);
        }

        public async Task<bool> SendCommandAndAwaitOkAsync(string text)
        {
            // Wireless: dịch chuỗi cấu hình text sang saveConfig/applyConfig và chờ xác nhận của thiết bị.
            if (WirelessActive)
            {
                return await _wirelessProxy!.SendCommandAndAwaitOkAsync(text).ConfigureAwait(false);
            }

            return await SendAndAwaitOkAsync(text).ConfigureAwait(false);
        }

        /// <summary>Marker firmware in ra NGAY SAU khi ghi FRAM xong (lệnh <c>Save&amp;Reboot:1</c>).</summary>
        private const string AllSettingsSavedMarker = "All Setting Saved";
        private TaskCompletionSource<bool>? _allSettingsSavedTcs;

        /// <summary>
        /// Tạo sẵn chỗ chờ marker "All Setting Saved" — PHẢI gọi TRƯỚC khi gửi lệnh Save&amp;Reboot, vì
        /// firmware in marker ngay khi ghi FRAM xong (có thể tới trước khi ta bắt đầu chờ).
        /// </summary>
        public void ArmAllSettingsSavedWaiter()
        {
            _allSettingsSavedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        /// <summary>
        /// Chờ marker "All Setting Saved" trong <paramref name="timeoutMs"/>. Chỉ khi trả về <c>true</c>
        /// mới được coi là ĐÃ LƯU THÀNH CÔNG vào FRAM (và mới được phép reset ESP qua EN pin).
        /// </summary>
        public async Task<bool> WaitForAllSettingsSavedAsync(int timeoutMs = 6000)
        {
            var tcs = _allSettingsSavedTcs
                      ?? new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _allSettingsSavedTcs = tcs;

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)).ConfigureAwait(false);
            return completed == tcs.Task && tcs.Task.Result;
        }

        private async Task StartHandshakeAndConnectionChecksAsync()
        {
            await SendAndAwaitOkAsync(InitialHandshakeCommand).ConfigureAwait(false);

            if (IsConnected)
            {
                StartConnectionCheckTimer();
            }
        }

        private void StartConnectionCheckTimer()
        {
            if (_connectionCheckTimer != null)
            {
                return;
            }

            _connectionCheckTimer = new System.Timers.Timer(_pollingIntervalMilliseconds)
            {
                AutoReset = true
            };
            _connectionCheckTimer.Elapsed += (_, _) => _ = RunConnectionCheckAsync();
            _connectionCheckTimer.Start();
        }

        private void StopConnectionCheckTimer()
        {
            if (_connectionCheckTimer == null)
            {
                return;
            }

            _connectionCheckTimer.Stop();
            _connectionCheckTimer.Dispose();
            _connectionCheckTimer = null;
            Interlocked.Exchange(ref _connectionCheckInProgress, 0);
            StopLinkWatchdog();
        }

        /// <summary>Đánh dấu vừa nhận dữ liệu từ thiết bị (mọi transport) — nuôi watchdog.</summary>
        private void TouchLinkAlive()
        {
            _lastRxUtc = DateTime.UtcNow;
            StartLinkWatchdog();   // lazy: tự bật ở dữ liệu ĐẦU TIÊN sau khi kết nối
        }

        private void StartLinkWatchdog()
        {
            if (_linkWatchdogTimer != null)
            {
                return;
            }

            _linkWatchdogTimer = new System.Timers.Timer(1000) { AutoReset = true };
            _linkWatchdogTimer.Elapsed += (_, _) => CheckLinkWatchdog();
            _linkWatchdogTimer.Start();
            Logger.Info("[MLAstro] Link watchdog started (auto-disconnect when the device goes silent)");
        }

        private void StopLinkWatchdog()
        {
            if (_linkWatchdogTimer == null)
            {
                return;
            }

            _linkWatchdogTimer.Stop();
            _linkWatchdogTimer.Dispose();
            _linkWatchdogTimer = null;
            _lastRxUtc = DateTime.UtcNow;
        }

        /// <summary>
        /// Chạy mỗi giây: handshake OK mà thiết bị im lặng quá LinkWatchdogTimeoutMs => tự ngắt kết nối.
        /// Bỏ qua khi: chưa kết nối · chưa handshake xong (để luồng handshake tự báo NO ANSWER rồi xử lý) ·
        /// poll '?' đang tạm dừng trên Serial (TPPA mượn cổng — thiết bị im lặng là CHỦ Ý).
        /// </summary>
        private void CheckLinkWatchdog()
        {
            if (Interlocked.Exchange(ref _linkWatchdogChecking, 1) == 1)
            {
                return;
            }

            try
            {
                if (!IsConnected)
                {
                    return;
                }

                if (!string.Equals(HandshakeStatus, "OK!", StringComparison.Ordinal))
                {
                    return;
                }

                if (!WirelessActive && PauseQueryGlobal)
                {
                    return;
                }

                var silentFor = DateTime.UtcNow - _lastRxUtc;
                if (silentFor.TotalMilliseconds < LinkWatchdogTimeoutMs)
                {
                    return;
                }

                var transport = WirelessActive ? "wireless (WebSocket)" : "serial (COM)";
                Logger.Warning($"[MLAstro] Link watchdog: no data from device for {silentFor.TotalSeconds:0.0}s ({transport}) - auto-disconnecting");
                AppendTerminalEntry(SerialTerminalEntry.Disconnected("Disconnected: no data from device (link watchdog)"));

                try
                {
                    NINA.Core.Utility.Notification.Notification.ShowWarning(
                        "MLAstro device stopped responding - the connection was closed automatically. Check power / Wi-Fi and reconnect.");
                }
                catch
                {
                }

                Disconnect();
            }
            catch (Exception ex)
            {
                Logger.Warning($"[MLAstro] Link watchdog check failed: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _linkWatchdogChecking, 0);
            }
        }

        private void StartDeviceChangeWatcher()
        {
            if (_deviceChangeWatcher != null)
            {
                return;
            }

            try
            {
                _deviceChangeWatcher = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_DeviceChangeEvent"));
                _deviceChangeWatcher.EventArrived += OnDeviceChangeEvent;
                _deviceChangeWatcher.Start();
                Logger.Info("[MLAstro] Device change watcher started");
            }
            catch (Exception ex)
            {
                Logger.Warning($"[MLAstro] Failed to start device change watcher: {ex.Message}");
                _deviceChangeWatcher = null;
            }
        }

        private void StopDeviceChangeWatcher()
        {
            if (_deviceChangeWatcher == null)
            {
                return;
            }

            try
            {
                _deviceChangeWatcher.EventArrived -= OnDeviceChangeEvent;
                _deviceChangeWatcher.Stop();
                _deviceChangeWatcher.Dispose();
            }
            catch (Exception ex)
            {
                Logger.Warning($"[MLAstro] Failed to stop device change watcher: {ex.Message}");
            }
            finally
            {
                _deviceChangeWatcher = null;
            }
        }

        private void OnDeviceChangeEvent(object sender, EventArrivedEventArgs e)
        {
            try
            {
                // The OS reports a device arrival/removal (same notification the serial debug
                // tools use). If our connected COM port is gone, disconnect right away.
                if (_serialPort == null)
                {
                    return;
                }

                var portGone = !_serialPort.IsOpen
                    || !SerialPort.GetPortNames().Contains(_serialPort.PortName, StringComparer.OrdinalIgnoreCase);
                if (portGone)
                {
                    Logger.Info("[MLAstro] Device change event: connected COM port is gone - auto disconnecting");
                    Disconnect();
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"[MLAstro] Device change event handling failed: {ex.Message}");
            }
        }

        private async Task RunConnectionCheckAsync()
        {
            // Already fully disconnected - nothing to monitor.
            if (_serialPort == null)
            {
                return;
            }

            if (Interlocked.Exchange(ref _connectionCheckInProgress, 1) == 1)
            {
                return;
            }

            try
            {
                // Case 1: the OS/driver closed the port handle (some Windows 10 systems do
                // this right after the USB adapter is unplugged). Finalize the disconnect so
                // the UI no longer shows a stale "Connected: ..." status.
                if (!_serialPort.IsOpen)
                {
                    Logger.Warning("[MLAstro] Serial port no longer open - auto disconnecting");
                    Disconnect();
                    return;
                }

                // Case 2: direct physical-unplug detection - when the USB cable is removed the
                // .NET SerialPort usually keeps reporting IsOpen=true (a "ghost" port), so we
                // check whether the OS still enumerates the connected COM port. This runs even
                // while '?' polling is paused because it sends nothing.
                if (IsComPortMissing())
                {
                    Logger.Warning($"[MLAstro] COM port {_serialPort.PortName} no longer present - auto disconnecting");
                    Disconnect();
                    return;
                }

                // Skip polling if paused globally
                if (PauseQueryGlobal)
                {
                    return;
                }

                // The device is alive if it answers with ANY line within the timeout.
                // This avoids the old bug where '?' returns telemetry (not "ok"), so waiting
                // for an explicit "ok" could spuriously report "NO ANSWER".
                var alive = await SendAndAwaitAnyAsync(ConnectionCheckCommand).ConfigureAwait(false);

                if (alive)
                {
                    _connectionCheckFailures = 0;
                    UpdateHandshakeStatus(true);
                    return;
                }

                // Fallback: the device is not answering. Auto-disconnect after a few consecutive
                // misses so the UI flips back to "Connect" without a manual click.
                _connectionCheckFailures++;
                if (_connectionCheckFailures >= ConnectionCheckFailThreshold)
                {
                    Logger.Warning($"[MLAstro] No serial response for {_connectionCheckFailures} consecutive polls - auto disconnecting");
                    Disconnect();
                    return;
                }

                UpdateHandshakeStatus(false);
            }
            finally
            {
                Interlocked.Exchange(ref _connectionCheckInProgress, 0);
            }
        }

        /// <summary>
        /// Returns true when the currently-connected COM port is no longer enumerated by the OS,
        /// which indicates the USB serial adapter has been physically unplugged.
        /// </summary>
        private bool IsComPortMissing()
        {
            try
            {
                if (_serialPort == null || !_serialPort.IsOpen)
                {
                    return false;
                }

                var portName = _serialPort.PortName;
                if (string.IsNullOrWhiteSpace(portName))
                {
                    return false;
                }

                return !SerialPort.GetPortNames().Contains(portName, StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private async Task<bool> SendAndAwaitOkAsync(string text)
        {
            if (!IsConnected || string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            TaskCompletionSource<bool> pending = null!;

            await _serialOperationSemaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!IsConnected || _serialPort == null)
                {
                    return false;
                }

                pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_responseSync)
                {
                    _pendingCommandTcs = pending;
                }

                var data = _serialPort.Encoding.GetBytes(text);
                WriteBytes(data);
                AppendTerminalEntry(SerialTerminalEntry.Sent(data, _serialPort.Encoding, HexDisplay));

                var completedTask = await Task.WhenAny(pending.Task, Task.Delay(HandshakeTimeoutMilliseconds)).ConfigureAwait(false);

                var gotOk = completedTask == pending.Task && pending.Task.Result;
                UpdateHandshakeStatus(gotOk);
                return gotOk;
            }
            catch (Exception ex)
            {
                Logger.Warning($"[MLAstro] Serial handshake failed: {ex.Message}");
                return false;
            }
            finally
            {
                lock (_responseSync)
                {
                    if (ReferenceEquals(_pendingCommandTcs, pending))
                    {
                        _pendingCommandTcs = null;
                    }
                }

                _serialOperationSemaphore.Release();
            }
        }

        private async Task<bool> SendAndAwaitAnyAsync(string text)
        {
            if (!IsConnected || string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            TaskCompletionSource<bool> any = null!;

            await _serialOperationSemaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!IsConnected || _serialPort == null)
                {
                    return false;
                }

                any = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_responseSync)
                {
                    _anyResponseTcs = any;
                }

                var data = _serialPort.Encoding.GetBytes(text);
                WriteBytes(data);
                AppendTerminalEntry(SerialTerminalEntry.Sent(data, _serialPort.Encoding, HexDisplay));

                var completedTask = await Task.WhenAny(any.Task, Task.Delay(HandshakeTimeoutMilliseconds)).ConfigureAwait(false);
                return completedTask == any.Task;
            }
            catch (Exception ex)
            {
                Logger.Warning($"[MLAstro] Serial connection check failed: {ex.Message}");
                return false;
            }
            finally
            {
                lock (_responseSync) { _anyResponseTcs = null; }
                _serialOperationSemaphore.Release();
            }
        }

        private void TryUpdateDeviceInfo(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            var firmwareMatch = Regex.Match(text, @"firmware\s+(\d+\.\d+\.\d+)", RegexOptions.IgnoreCase);
            if (firmwareMatch.Success)
            {
                FirmwareVersion = firmwareMatch.Groups[1].Value;
                Logger.Info($"[MLAstro] Device firmware detected: {FirmwareVersion}");
            }
        }

        private void ProcessWifiPasswordResponses(string receivedText)
        {
            if (string.IsNullOrWhiteSpace(receivedText))
            {
                return;
            }

            try
            {
                // Regex cho phép giá trị RỖNG ([^\r\n]*): mật khẩu rỗng trên thiết bị cũng là thông tin
                // ĐÚNG cần phản ánh vào settings. Trước đây bỏ qua giá trị rỗng/không khớp nên UI cứ
                // hiện mật khẩu cũ trong khi thiết bị đã mất mật khẩu (nguyên nhân "STA fail reason 15"
                // mà tưởng là lỗi router).
                var apMatch = Regex.Match(receivedText, @"\bAPpa:([^\r\n]*)");
                if (apMatch.Success)
                {
                    var value = apMatch.Groups[1].Value.Trim();
                    if (value != "?")
                    {
                        _settings.ApPass = value;
                        Logger.Info($"[MLAstro] AP password synced from device ({(value.Length == 0 ? "empty" : "set")})");
                    }
                }

                var staMatch = Regex.Match(receivedText, @"\bSTAp:([^\r\n]*)");
                if (staMatch.Success)
                {
                    var value = staMatch.Groups[1].Value.Trim();
                    if (value != "?")
                    {
                        _settings.WifiPass = value;
                        Logger.Info($"[MLAstro] Station password synced from device ({(value.Length == 0 ? "empty" : "set")})");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"[MLAstro] WiFi password response parsing failed: {ex.Message}");
            }
        }

        // =====================================================================
        // Transport WIRELESS (WebSocket)
        // Dữ liệu nhận từ WebSocket được tổng hợp lại thành ĐÚNG định dạng text của firmware
        // serial rồi bơm vào CÙNG pipeline xử lý ở đây. Nhờ vậy TelemetryParser,
        // TelemetryDataReceived, CompletionReceived, ErrorStateChanged và toàn bộ UI
        // (CONTROL + HARDWARE SETTING) hoạt động y như khi dùng cổng COM.
        // =====================================================================

        /// <summary>Bơm một dòng text đã tổng hợp từ WebSocket (telemetry / ok / ERROR: / COMPLETED).</summary>
        public void InjectIncomingText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            TouchLinkAlive();   // nuôi watchdog: vừa có dữ liệu từ thiết bị qua WebSocket
            try { ProcessTelemetryData(text); }
            catch (Exception ex) { Logger.Warning($"[MLAstro][WS] Telemetry inject failed: {ex.Message}"); }

            // Chỉ dòng ERROR: của firmware mới là mã lỗi (ProcessErrorTelemetry cũng tự bảo vệ).
            if (text.StartsWith("ERROR:", StringComparison.Ordinal))
            {
                try { ProcessErrorTelemetry(text); }
                catch { }
            }

            try { CheckForCompletionEvents(text); }
            catch { }

            try { ProcessWifiPasswordResponses(text); }
            catch { }

            try { RaiseExternalLine(text.TrimEnd('\r', '\n')); }
            catch { }
        }

        private void CheckForCompletionEvents(string receivedText)
        {
            if (string.IsNullOrWhiteSpace(receivedText))
            {
                return;
            }

            // Check for completion messages
            if (receivedText.Contains("COMPLETED"))
            {
                if (receivedText.Contains("AzAN:COMPLETED"))
                {
                    CompletionReceived?.Invoke(this, "AzAN");
                    Logger.Info("[MLAstro] Azimuth alignment completed");
                }
                else if (receivedText.Contains("AlAN:COMPLETED"))
                {
                    CompletionReceived?.Invoke(this, "AlAN");
                    Logger.Info("[MLAstro] Altitude alignment completed");
                }
                else if (receivedText.Contains("AAll:COMPLETED"))
                {
                    CompletionReceived?.Invoke(this, "AAll");
                    Logger.Info("[MLAstro] All alignment completed");
                }
                else if (receivedText.Contains("HOME_COMPLETED"))
                {
                    CompletionReceived?.Invoke(this, "HOME");
                    Logger.Info("[MLAstro] Home return completed");
                }
            }
        }

        private void ProcessTelemetryData(string receivedText)
        {
            if (string.IsNullOrWhiteSpace(receivedText))
            {
                return;
            }

            try
            {
                _telemetryBuffer.Append(receivedText);
                var bufferContent = _telemetryBuffer.ToString();

                // Check if we have a complete telemetry message: <...>...
                if (bufferContent.Contains('<') && bufferContent.Contains('>'))
                {
                    var startIdx = bufferContent.IndexOf('<');
                    var endIdx = bufferContent.IndexOf('>', startIdx);

                    if (endIdx > startIdx)
                    {
                        // Find end of telemetry data (newline after the data section)
                        var lineEndIdx = bufferContent.IndexOfAny(new[] { '\r', '\n' }, endIdx);
                        if (lineEndIdx > endIdx || bufferContent.Length > endIdx + 100) // Complete or long enough
                        { 
                            var telemetryLine = lineEndIdx > 0 
                                ? bufferContent.Substring(startIdx, lineEndIdx - startIdx)
                                : bufferContent.Substring(startIdx);

                            Logger.Info($"[MLAstro] Processing telemetry line: {telemetryLine.Substring(0, Math.Min(100, telemetryLine.Length))}...");

                            // Parse telemetry and raise event
                            var telemetryData = ParseTelemetryLine(telemetryLine);
                            if (telemetryData != null)
                            {
                                Logger.Info($"[MLAstro] Telemetry parsed - Status: {telemetryData.Status}, AzPos: {telemetryData.AzPosition}, AltPos: {telemetryData.AltPosition}");
                                InvokeOnUiThread(() => TelemetryDataReceived?.Invoke(this, new TelemetryDataEventArgs(telemetryData)));
                            }
                            else
                            {
                                Logger.Warning("[MLAstro] ParseTelemetryLine returned null");
                            }

                            // Only update settings if not paused and not suspended (user is editing)
                            if (!PauseTelemetryUpdates && !SuspendSettingsSync)
                            {
                                _applyingTelemetrySettings = true;
                                try
                                {
                                    TelemetryParser.ParseAndApplySettings(telemetryLine, _settings);
                                }
                                finally
                                {
                                    _applyingTelemetrySettings = false;
                                }
                                Logger.Info("[MLAstro] Telemetry data received and parsed");
                            }

                            // Clear buffer after successful parse
                            _telemetryBuffer.Clear();
                            if (lineEndIdx > 0 && lineEndIdx < bufferContent.Length - 1)
                            {
                                _telemetryBuffer.Append(bufferContent.Substring(lineEndIdx + 1));
                            }
                        }
                    }
                }
                else if (_telemetryBuffer.Length > 1000)
                {
                    // Buffer too large without complete telemetry, clear it
                    _telemetryBuffer.Clear();
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"[MLAstro] Telemetry processing failed: {ex.Message}");
                _telemetryBuffer.Clear();
            }
        }

        private void UpdateHandshakeStatus(bool isOk)
        {
            InvokeOnUiThread(() => HandshakeStatus = isOk ? "OK!" : "NO ANSWER");
        }

        private void ResetPendingHandshakeState()
        {
            StopConnectionCheckTimer();

            lock (_responseSync)
            {
                _pendingCommandTcs?.TrySetResult(false);
                _pendingCommandTcs = null;
                _anyResponseTcs?.TrySetResult(false);
                _anyResponseTcs = null;
            }
        }

        private void AppendTerminalEntry(SerialTerminalEntry entry)
        {
            InvokeOnUiThread(() =>
            {

                // Mới nhất lên ĐẦU (index 0), cũ nhất dần về CUỐI.
                TerminalEntries.Insert(0, entry);
                while (TerminalEntries.Count > MaxTerminalEntries)
                {
                    TerminalEntries.RemoveAt(TerminalEntries.Count - 1); // bỏ entry cũ nhất (ở cuối)
                }
            });
        }

        private void RefreshTerminalDisplay()
        {
            InvokeOnUiThread(() =>
            {
                foreach (var entry in TerminalEntries)
                {
                    entry.SetHexDisplay(HexDisplay);
                }
            });
        }

        private void InvokeOnUiThread(Action action)
        {
            if (Application.Current?.Dispatcher != null && !Application.Current.Dispatcher.CheckAccess())
            {
                Application.Current.Dispatcher.BeginInvoke(action);
                return;
            }

            action();
        }

        private void DisconnectPortInstance()
        {
            try
            {
                ResetPendingHandshakeState();
                ResetErrorState();
                StopDeviceChangeWatcher();

                if (_serialPort != null)
                {
                    _serialPort.DataReceived -= OnSerialPortDataReceived;
                }

                if (_serialPort?.IsOpen == true)
                {
                    _serialPort.Close();
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"[MLAstro] Serial disconnect failed: {ex.Message}");
            }
            finally 
            {
                _serialPort?.Dispose();
                _serialPort = null!;
            }
        }

        public void Dispose()
        {
            Logger.Info("[MLAstro] SerialConnectionService disposing...");

            // Stop connection check timer first
            StopConnectionCheckTimer();
            StopDeviceChangeWatcher();

            // Reset pending handshake state
            ResetPendingHandshakeState();

            // Clear event subscribers to prevent memory leaks
            TelemetryDataReceived = null;
            CompletionReceived = null;
            PropertyChanged = null;

            // Clear terminal entries
            InvokeOnUiThread(() => TerminalEntries.Clear());

            // Dispose semaphore
            _serialOperationSemaphore.Dispose();

            // Disconnect and dispose serial port
            DisconnectPortInstance();

            // Clear static instance reference to allow GC
            lock (_instanceLock)
            {
                if (ReferenceEquals(_instance, this))
                {
                    _instance = null;
                }
            }

            Logger.Info("[MLAstro] SerialConnectionService disposed");
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null!)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private TelemetryData? ParseTelemetryLine(string telemetryLine)
        {
            if (string.IsNullOrWhiteSpace(telemetryLine))
            {
                Logger.Info("[MLAstro] ParseTelemetryLine: telemetryLine is null or empty");
                return null;
            }

            try
            {
                // Format: <STATUS|Mpos:+-X.XXXXX,+/-Y.YYYYY|>DATA_SETTING
                var startIdx = telemetryLine.IndexOf('<');
                var endIdx = telemetryLine.IndexOf('>');

                if (startIdx < 0 || endIdx < 0 || endIdx <= startIdx)
                {
                    Logger.Info($"[MLAstro] ParseTelemetryLine: Invalid format - startIdx={startIdx}, endIdx={endIdx}");
                    return null;
                }

                var headerSection = telemetryLine.Substring(startIdx + 1, endIdx - startIdx - 1);
                Logger.Info($"[MLAstro] ParseTelemetryLine: Header section = {headerSection}");

                var parts = headerSection.Split('|');

                if (parts.Length < 2)
                {
                    Logger.Info($"[MLAstro] ParseTelemetryLine: Not enough parts - {parts.Length}");
                    return null;
                }

                var data = new TelemetryData
                {
                    Status = parts[0].Trim()
                };

                // Parse Mpos from header (format: Mpos:+-X.XXXXX,+/-Y.YYYYY)
                foreach (var part in parts)
                {
                    if (part.StartsWith("Mpos:"))
                    {
                        ParseMovedPosition(part, data);
                        break;
                    }
                }

                // Parse DATA_SETTING section after '>' (contains AzPH, AlPH for position from home)
                if (endIdx < telemetryLine.Length - 1)
                {
                    var dataSection = telemetryLine.Substring(endIdx + 1);
                    Logger.Info($"[MLAstro] ParseTelemetryLine: Data section length = {dataSection.Length}");
                    ParseDataSettings(dataSection, data);
                }

                // Convert AzPH/AlPH decimal degrees to display format for position from home
                data.AzPosition = FormatDegreesToDMS(data.AzPositionDegrees);
                data.AltPosition = FormatDegreesToDMS(data.AltPositionDegrees);

                Logger.Info($"[MLAstro] ParseTelemetryLine: Parsed Status={data.Status}, AzPos={data.AzPosition}, AltPos={data.AltPosition}");

                return data;
            }
            catch (Exception ex)
            {
                Logger.Warning($"[MLAstro] Parse telemetry header failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Converts decimal degrees (e.g., +1.234567 or -0.567890) to DMS format for display.
        /// Format: AzPH:+/-X.XXXXX -> "+X° MM' SS\""
        /// </summary>
        private string FormatDegreesToDMS(double decimalDegrees)
        {
            try
            {
                var isNegative = decimalDegrees < 0;
                var absValue = Math.Abs(decimalDegrees);

                var degrees = (int)absValue;
                var remainder = (absValue - degrees) * 60;
                var minutes = (int)remainder;
                var seconds = (int)Math.Round((remainder - minutes) * 60);

                // Handle seconds overflow
                if (seconds >= 60)
                {
                    seconds -= 60;
                    minutes++;
                }
                if (minutes >= 60)
                {
                    minutes -= 60;
                    degrees++;
                }

                var sign = isNegative ? "-" : "+";
                return $"{sign}{degrees}° {minutes:D2}' {seconds:D2}\"";
            }
            catch
            {
                return "+0° 00' 00\"";
            }
        }

        /// <summary>
        /// Parses Mpos field from header: Mpos:+-X.XXXXX,+/-Y.YYYYY
        /// where X = Azimuth moved, Y = Altitude moved (relative to alignment start)
        /// </summary>
        private void ParseMovedPosition(string mposData, TelemetryData data)
        {
            try
            {
                // Format: Mpos:+-X.XXXXX,+/-Y.YYYYY
                var colonIdx = mposData.IndexOf(':');
                if (colonIdx < 0) return;

                var values = mposData.Substring(colonIdx + 1).Split(',');
                if (values.Length != 2) return;

                if (double.TryParse(values[0].Trim(), NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var azMoved))
                {
                    data.AzMovedDegrees = azMoved;
                    data.AzMovedPosition = FormatDegreesToDMS(azMoved);
                }

                if (double.TryParse(values[1].Trim(), NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var altMoved))
                {
                    data.AltMovedDegrees = altMoved;
                    data.AltMovedPosition = FormatDegreesToDMS(altMoved);
                }

                Logger.Info($"[MLAstro] ParseMovedPosition: Az={data.AzMovedPosition}, Alt={data.AltMovedPosition}");
            }
            catch (Exception ex)
            {
                Logger.Warning($"[MLAstro] ParseMovedPosition failed: {ex.Message}");
            }
        }

        private void ParseDataSettings(string dataSection, TelemetryData data)
        {
            if (string.IsNullOrWhiteSpace(dataSection) || data == null)
            {
                return;
            }

            try
            {
                var parameters = dataSection.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

                foreach (var param in parameters)
                {
                    var parts = param.Split(new[] { ':' }, 2);
                    if (parts.Length != 2)
                    {
                        continue;
                    }

                    var key = parts[0].Trim();
                    var value = parts[1].Trim();

                    switch (key)
                    {
                        // System
                        case "SLvl":
                            if (int.TryParse(value, out var speedLevel))
                                data.SpeedLevel = speedLevel;
                            break;
                        case "WSta":
                            if (int.TryParse(value, out var wifiStatus))
                                data.WifiConnected = wifiStatus == 1;
                            break;
                        // Chất lượng đường STA: 0 = chưa vào router, 1 = có router nhưng không internet,
                        // 2 = có internet (firmware dò bằng TCP probe; token WQu do firmware serial phát
                        // hoặc do MlastroWebSocketService chuyển từ field `sta_qual`).
                        case "WQu":
                            if (int.TryParse(value, out var staQuality))
                                data.StaQuality = staQuality;
                            break;

                        // Relative Mode
                        case "JoRe":
                            if (int.TryParse(value, out var joRe))
                                data.IsRelativeMode = joRe == 1;
                            break;
                        case "ReDe":
                            if (int.TryParse(value, out var reDe))
                                data.RelativeDegrees = reDe;
                            break;
                        case "ReAM":
                            if (int.TryParse(value, out var reAm))
                                data.RelativeMinutes = reAm;
                            break;
                        case "ReAS":
                            if (int.TryParse(value, out var reAs))
                                data.RelativeSeconds = reAs;
                            break;

                        // Azimuth
                        case "AzPH":
                            if (double.TryParse(value, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var azPh))
                                data.AzPositionDegrees = azPh;
                            break;
                        case "AzSD":
                            if (double.TryParse(value, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var azSd))
                                data.AzStepsPerDegree = azSd;
                            break;

                        // Altitude
                        case "AlPH":
                            if (double.TryParse(value, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var alPh))
                                data.AltPositionDegrees = alPh;
                            break;
                        case "AlSD":
                            if (double.TryParse(value, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var alSd))
                                data.AltStepsPerDegree = alSd;
                            break;

                        // WiFi Info
                        case "STAi":
                            data.StationIP = value;
                            break;

                        // AP của thiết bị (hotspot): IP + "đã lên hay chưa" — dùng cho dòng
                        // "AP: Connected/Ready/Error <IP>" trên HeaderBar.
                        case "APip":
                            data.ApIp = value;
                            break;
                        case "APrd":
                            data.ApReady = value == "1";
                            break;

                        // Home status (Read-Only from hardware)
                        case "Home":
                            if (int.TryParse(value, out var home))
                                data.IsHomed = home == 1;
                            break;

                        // Alignment directions (Read/Write)
                        case "AzDi":
                            if (int.TryParse(value, out var azDi))
                                data.AzDirection = azDi == 1;
                            break;
                        case "AlDi":
                            if (int.TryParse(value, out var alDi))
                                data.AltDirection = alDi == 1;
                            break;

                        // Alignment error values
                        case "AzED":
                            if (int.TryParse(value, out var azED))
                                data.AzErrorDegrees = azED;
                            break;
                        case "AzEM":
                            if (int.TryParse(value, out var azEM))
                                data.AzErrorMinutes = azEM;
                            break;
                        case "AzES":
                            if (int.TryParse(value, out var azES))
                                data.AzErrorSeconds = azES;
                            break;
                        case "AlED":
                            if (int.TryParse(value, out var alED))
                                data.AltErrorDegrees = alED;
                            break;
                        case "AlEM":
                            if (int.TryParse(value, out var alEM))
                                data.AltErrorMinutes = alEM;
                            break;
                        case "AlES":
                            if (int.TryParse(value, out var alES))
                                data.AltErrorSeconds = alES;
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"[MLAstro] Parse data settings failed: {ex.Message}");
            }
        }
    }

    public class TelemetryData
    {
        public string Status { get; set; } = null!;
        public string AzPosition { get; set; } = null!;
        public string AltPosition { get; set; } = null!;

        // System
        public int SpeedLevel { get; set; } = 3;
        public bool WifiConnected { get; set; }
        /// <summary>Chất lượng đường STA: 0 = chưa vào router, 1 = có router nhưng không internet, 2 = có internet.</summary>
        public int StaQuality { get; set; }
        public bool IsHomed { get; set; }

        // Mode
        public bool IsRelativeMode { get; set; }
        public int RelativeDegrees { get; set; }
        public int RelativeMinutes { get; set; }
        public int RelativeSeconds { get; set; }

        // Positions (in degrees) - from home
        public double AzPositionDegrees { get; set; }
        public double AltPositionDegrees { get; set; }

        // Moved positions (in degrees) - relative to alignment start
        public double AzMovedDegrees { get; set; }
        public double AltMovedDegrees { get; set; }
        public string AzMovedPosition { get; set; } = null!;
        public string AltMovedPosition { get; set; } = null!;

        // Steps configuration
        public double AzStepsPerDegree { get; set; } = 1.0;
        public double AltStepsPerDegree { get; set; } = 1.0;

        // Alignment directions (1 = Right/Up, 0 = Left/Down)
        public bool AzDirection { get; set; }
        public bool AltDirection { get; set; }

        // Alignment error values
        public int AzErrorDegrees { get; set; }
        public int AzErrorMinutes { get; set; }
        public int AzErrorSeconds { get; set; }
        public int AltErrorDegrees { get; set; }
        public int AltErrorMinutes { get; set; }
        public int AltErrorSeconds { get; set; }

        // Network
        public string StationIP { get; set; } = null!;
        /// <summary>IP hotspot (AP) của thiết bị (token APip).</summary>
        public string ApIp { get; set; } = null!;
        /// <summary>AP của thiết bị đã lên và có IP chưa (token APrd: 1 = có).</summary>
        public bool ApReady { get; set; }
    }

    public class TelemetryDataEventArgs : EventArgs
    {
        public TelemetryData Data { get; }

        public TelemetryDataEventArgs(TelemetryData data)
        {
            Data = data;
        }
    }

    public class SerialTerminalEntry : INotifyPropertyChanged
    {
        private readonly byte[]? _payload;
        private readonly string? _statusText;
        private readonly Encoding _encoding;
        private bool _hexDisplay;
        private string _displayText;

        private SerialTerminalEntry(SerialTerminalEntryType entryType, byte[]? payload, string? statusText, Encoding encoding, bool hexDisplay)
        {
            EntryType = entryType;
            _payload = payload;
            _statusText = statusText;
            _encoding = encoding ?? Encoding.UTF8;
            _hexDisplay = hexDisplay;
            _displayText = FormatDisplayText();
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public SerialTerminalEntryType EntryType { get; }

        public string DisplayText
        {
            get => _displayText;
            private set
            {
                if (_displayText != value)
                {
                    _displayText = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayText)));
                }
            }
        }

        public Brush Foreground => EntryType switch
        {
            SerialTerminalEntryType.Sent => Brushes.DeepPink,
            SerialTerminalEntryType.Connected => Brushes.LimeGreen,
            SerialTerminalEntryType.Disconnected => Brushes.IndianRed,
            _ => Brushes.Gray
        };

        /// <summary>
        /// Nhãn đánh dấu loại nội dung hiển thị ở đầu dòng:
        /// TX: (Sent) / RX: (Received) / 🔔 (Connected &amp; Disconnected — thông báo, không phải TX/RX).
        /// </summary>
        public string Marker => EntryType switch
        {
            SerialTerminalEntryType.Sent => "TX: ",
            SerialTerminalEntryType.Received => "RX: ",
            SerialTerminalEntryType.Connected => "🔔 ",
            SerialTerminalEntryType.Disconnected => "🔔 ",
            _ => string.Empty
        };

        public static SerialTerminalEntry Sent(byte[] payload, Encoding encoding, bool hexDisplay)
            => new(SerialTerminalEntryType.Sent, payload, null, encoding, hexDisplay);

        public static SerialTerminalEntry Received(byte[] payload, Encoding encoding, bool hexDisplay)
            => new(SerialTerminalEntryType.Received, payload, null, encoding, hexDisplay);

        public static SerialTerminalEntry Connected(string text)
            => new(SerialTerminalEntryType.Connected, null, text, Encoding.UTF8, false);

        public static SerialTerminalEntry Disconnected(string text)
            => new(SerialTerminalEntryType.Disconnected, null, text, Encoding.UTF8, false);

        public void SetHexDisplay(bool hexDisplay)
        {
            if (_hexDisplay == hexDisplay)
            {
                return;
            }

            _hexDisplay = hexDisplay;
            DisplayText = FormatDisplayText();
        }

        private string FormatDisplayText()
        {
            if (EntryType == SerialTerminalEntryType.Connected || EntryType == SerialTerminalEntryType.Disconnected)
            {
                return _statusText ?? string.Empty;
            }

            if (_payload == null || _payload.Length == 0)
            {
                return string.Empty;
            }

            if (_hexDisplay)
            {
                return string.Join(" ", _payload.Select(b => b.ToString("X2")));
            }

            return _encoding.GetString(_payload).TrimEnd('\r', '\n');
        }
    }

    public enum SerialTerminalEntryType
    {
        Received,
        Sent,
        Connected,
        Disconnected
    }

    public static class TelemetryParser
    {
        public static void ParseAndApplySettings(string telemetryData, PluginSettings settings)
        {
            if (string.IsNullOrWhiteSpace(telemetryData) || settings == null)
            {
                return;
            }

            try
            {
                // Format: <STATUS|AzMP:D,M,S|AlMP:D,M,S|>DATA_SETTING
                var startIndex = telemetryData.IndexOf('>');
                if (startIndex < 0)
                {
                    return;
                }

                var dataSection = telemetryData.Substring(startIndex + 1);
                var parameters = dataSection.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

                Logger.Info($"[MLAstro] Parsing {parameters.Length} parameters from telemetry");

                foreach (var param in parameters)
                {
                    var parts = param.Split(new[] { ':' }, 2);
                    if (parts.Length != 2)
                    {
                        continue;
                    }

                    var key = parts[0].Trim();
                    var value = parts[1].Trim();

                    // Log WiFi-related parameters
                    if (key.StartsWith("STA") || key.StartsWith("AP"))
                    {
                        Logger.Info($"[MLAstro] WiFi param: {key} = {value}");
                    }

                    MapParameterToSettings(key, value, settings);
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"[MLAstro] Telemetry parse failed: {ex.Message}");
            }
        }

        private static void MapParameterToSettings(string key, string value, PluginSettings settings)
        {
            try
            {
                switch (key)
                {
                    // Soft Limits
                    case "AzL1":
                        if (double.TryParse(value, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var azMin))
                            settings.LimitAzMin = azMin;
                        break;
                    case "AzL2":
                        if (double.TryParse(value, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var azMax))
                            settings.LimitAzMax = azMax;
                        break;
                    case "AlL1":
                        if (double.TryParse(value, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var altMin))
                            settings.LimitAltMin = altMin;
                        break;
                    case "AlL2":
                        if (double.TryParse(value, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var altMax))
                            settings.LimitAltMax = altMax;
                        break;

                    // Azimuth Motor Settings
                    case "AzRD":
                        if (int.TryParse(value, out var azReverse))
                            settings.AzReverse = azReverse != 0;
                        break;
                    case "AzIR":
                        if (int.TryParse(value, out var azCurrentRun))
                            settings.AzCurrentRun = azCurrentRun;
                        break;
                    case "AzIH":
                        if (int.TryParse(value, out var azCurrentHold))
                            settings.AzCurrentHold = azCurrentHold;
                        break;
                    case "AzSB":
                        if (int.TryParse(value, out var azBooster))
                            settings.AzBooster = azBooster;
                        break;
                    case "AzSC":
                        if (int.TryParse(value, out var azCoolStep))
                            settings.AzCoolStep = azCoolStep;
                        break;
                    case "AzMS":
                        if (int.TryParse(value, out var azMicrosteps))
                            settings.AzMicrosteps = azMicrosteps;
                        break;
                    case "AzAc":
                        if (int.TryParse(value, out var azAccel))
                            settings.AzAccel = azAccel;
                        break;
                    case "AzDec":
                        if (int.TryParse(value, out var azDecel))
                            settings.AzDecel = azDecel;
                        break;
                    case "AzSD":
                        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var azStepsPerDegree))
                            settings.AzStepsPerDegree = azStepsPerDegree;
                        break;
                    case "AzRM":
                        if (int.TryParse(value, out var azMode))
                            settings.AzMode = azMode;
                        break;

                    // Altitude Motor Settings
                    case "AlRD":
                        if (int.TryParse(value, out var altReverse))
                            settings.AltReverse = altReverse != 0;
                        break;
                    case "AlIR":
                        if (int.TryParse(value, out var altCurrentRun))
                            settings.AltCurrentRun = altCurrentRun;
                        break;
                    case "AlIH":
                        if (int.TryParse(value, out var altCurrentHold))
                            settings.AltCurrentHold = altCurrentHold;
                        break;
                    case "AlSB":
                        if (int.TryParse(value, out var altBooster))
                            settings.AltBooster = altBooster;
                        break;
                    case "AlSC":
                        if (int.TryParse(value, out var altCoolStep))
                            settings.AltCoolStep = altCoolStep;
                        break;
                    case "AlMS":
                        if (int.TryParse(value, out var altMicrosteps))
                            settings.AltMicrosteps = altMicrosteps;
                        break;
                    case "AlAc":
                        if (int.TryParse(value, out var altAccel))
                            settings.AltAccel = altAccel;
                        break;
                    case "AlDe":
                        if (int.TryParse(value, out var altDecel))
                            settings.AltDecel = altDecel;
                        break;
                    case "AlSD":
                        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var altStepsPerDegree))
                            settings.AltStepsPerDegree = altStepsPerDegree;
                        break;
                    case "AlRM":
                        if (int.TryParse(value, out var altMode))
                            settings.AltMode = altMode;
                        break;

                    // Backlash
                    case "Back":
                        if (int.TryParse(value, out var backlashEnabled))
                            settings.BacklashEnabled = backlashEnabled != 0;
                        break;
                    case "AzBl":
                        if (int.TryParse(value, out var azBacklash))
                            settings.BacklashAz = azBacklash;
                        break;
                    case "AlBl":
                        if (int.TryParse(value, out var altBacklash))
                            settings.BacklashAlt = altBacklash;
                        break;

                    // P.A Overshoot
                    case "Over":
                        if (int.TryParse(value, out var overshootEnabled))
                            settings.OvershootEnabled = overshootEnabled != 0;
                        break;
                    case "OvUp":
                        if (int.TryParse(value, out var overshootUp))
                            settings.OvershootMoveUp = overshootUp != 0;
                        break;
                    case "OvDn":
                        if (int.TryParse(value, out var overshootDown))
                            settings.OvershootMoveDown = overshootDown != 0;
                        break;
                    case "OvD":
                        if (int.TryParse(value, out var overshootDegrees))
                            settings.OvershootDegrees = overshootDegrees;
                        break;
                    case "OvM":
                        if (int.TryParse(value, out var overshootMinutes))
                            settings.OvershootMinutes = overshootMinutes;
                        break;
                    case "OvS":
                        if (int.TryParse(value, out var overshootSeconds))
                            settings.OvershootSeconds = overshootSeconds;
                        break;

                    // WiFi Settings
                    case "APss":
                        settings.ApSsid = value;
                        break;
                    case "APpa":
                        settings.ApPass = value;
                        break;
                    case "APip":
                        settings.ApIp = value;
                        break;
                    case "STAs":
                        settings.WifiSsid = value;
                        break;
                    case "STAp":
                        settings.WifiPass = value;
                        break;
                    case "STAi":
                        settings.WifiIp = value;
                        Logger.Info($"[MLAstro] STAi mapped: {value}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"[MLAstro] Failed to map parameter {key}={value}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// A COM port entry for the CONNECTION dropdown: the raw port name (e.g. "COM4")
    /// used for connecting, plus a human-readable display name (e.g. "COM4 - USB-SERIAL CH340").
    /// </summary>
    public class ComPortInfo
    {
        public ComPortInfo(string portName, string? friendlyName)
        {
            PortName = portName;
            DisplayName = BuildDisplayName(portName, friendlyName);
        }

        public string PortName { get; }
        public string DisplayName { get; }

        private static string BuildDisplayName(string portName, string? friendlyName)
        {
            if (string.IsNullOrWhiteSpace(friendlyName))
            {
                return portName;
            }

            // Strip a trailing "(COMx)" from the friendly name to avoid duplication,
            // e.g. "USB-SERIAL CH340 (COM4)" -> "USB-SERIAL CH340", then prefix the port number.
            var cleaned = Regex.Replace(friendlyName, @"\s*\(COM\d+\)\s*$", string.Empty, RegexOptions.IgnoreCase).Trim();
            return string.IsNullOrWhiteSpace(cleaned) ? portName : $"{portName} - {cleaned}";
        }
    }

    /// <summary>
    /// Parsed snapshot of the firmware's dedicated error telemetry line ("ERROR:...").
    /// Each code maps to a value: 0 = OK, 1 = WARNING, 2 = ERROR.
    /// </summary>
    public class DriverErrorState
    {
        /// <summary>
        /// A reusable clean (no errors, no warnings) state. Instance is immutable, so sharing is safe.
        /// </summary>
        public static DriverErrorState Clean { get; } = new DriverErrorState(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));

        public DriverErrorState(IReadOnlyDictionary<string, int> codes)
        {
            Codes = codes;
        }

        public IReadOnlyDictionary<string, int> Codes { get; }

        public bool IsClean => Codes.Values.All(v => v == 0);

        public bool HasErrors => Codes.Values.Any(v => v == 2);

        public bool HasWarnings => Codes.Values.Any(v => v == 1);

        /// <summary>
        /// Human-readable English summary of all non-zero codes, e.g. "AZ driver not responding [ERROR]; ALT over-temperature pre-warning [WARNING]".
        /// Empty when clean.
        /// </summary>
        public string Summary
        {
            get
            {
                var parts = Codes
                    .Where(kv => kv.Value != 0)
                    .Select(kv => $"{Describe(kv.Key)} [{(kv.Value == 2 ? "ERROR" : "WARNING")}]");
                return string.Join("; ", parts);
            }
        }

        public static string Describe(string code) => code switch
        {
            "Sys" => "System error",
            "AzNC" => "AZ driver not responding",
            "AlNC" => "ALT driver not responding",
            "AzOT" => "AZ over-temperature",
            "AlOT" => "ALT over-temperature",
            "AzPW" => "AZ over-temperature pre-warning",
            "AlPW" => "ALT over-temperature pre-warning",
            "AzSA" => "AZ short to ground A",
            "AzSB" => "AZ short to ground B",
            "AlSA" => "ALT short to ground A",
            "AlSB" => "ALT short to ground B",
            "AzOL" => "AZ open load",
            "AlOL" => "ALT open load",
            "AzHL" => "AZ hard limit",
            "AlHL" => "ALT hard limit",
            "AzSL" => "AZ soft limit reached",
            "AlSL" => "ALT soft limit reached",
            "Esc" => "Hard-limit escape mode",
            // Lệnh bị TỪ CHỐI vì soft-limit — bitfield `CmdRf` của ERROR telemetry (xem
            // ProcessErrorTelemetry: mỗi bit được bung thành 1 mã riêng, giá trị 1 = WARNING).
            "RfRelAz" => "AZ relative move refused (soft limit)",
            "RfRelAl" => "ALT relative move refused (soft limit)",
            "RfAlnAz" => "AZ align target out of soft limit",
            "RfAlnAl" => "ALT align target out of soft limit",
            "RfJogAz" => "AZ jog refused (already at soft limit)",
            "RfJogAl" => "ALT jog refused (already at soft limit)",
            "RfAlnOv" => "ALT align overshoot leg out of soft limit",
            _ => code
        };
    }
}
