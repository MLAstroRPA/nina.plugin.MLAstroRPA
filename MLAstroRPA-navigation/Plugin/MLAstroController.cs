using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq; 
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using MLAstroRPA.Broker;
using MLAstroRPA.Dockables;
using MLAstroRPA.Services;
using MLAstroRPA.Settings;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
 
namespace MLAstroRPA.Plugin
{
    /// <summary>
    /// Merged MLAstroRPA+TPPA plugin: this is no longer a NINA plugin manifest. It is the MLAstro
    /// options/state controller that backs the CONTROL / CONNECTION / CONFIGURATION tabs of the
    /// combined plugin's options page. Owned (and disposed) by
    /// NINA.Plugins.MLAstroRPA.PolarizationPlugin (the single IPluginManifest of this assembly).
    /// </summary>
    public class MLAstroController : INotifyPropertyChanged, IDisposable
    {
        // Firmware MLAstroRPA chạy cố định 115200 8N1 - không cho người dùng chọn baudrate nữa.
        private const int SerialBaudRate = 115200;
        // Wireless (WebSocket): AutoReconnect (sau Reset ESP32 / SAVE ALL) chỉ thử lại trong 1 khoảng
        // CÓ HẠN này rồi báo lỗi - đủ cho firmware reboot + vào WiFi, nhưng địa chỉ sai thì không
        // retry vô hạn. (Lần kết nối do người dùng bấm Connect đã tự giới hạn 5 s trong ConnectAsync.)
        private const int WirelessReconnectWindowSeconds = 30;

        private readonly SerialConnectionService _serialConnectionService;
        private readonly PolarAlignmentDockVM _polarAlignmentDockVM;
        private readonly Action<bool> _onPauseQueryChanged;
        private ResourceDictionary? _pluginResourceDictionary;
        private FileSystemWatcher? _pluginFolderWatcher;
        private bool _disposed = false;
        private bool _isHexInputEnabled;
        private string _serialTerminalInput = string.Empty;
        private bool _isModifyMode;
        private bool _hasUserSettingsEdits;
        private string _autoReconnectStatus = string.Empty;
        private bool _isHandshakeSuccessful;
        private string? _savedSettingsSnapshot;
        private bool _showApPassword;
        private bool _showStaPassword;
        private bool _apPasswordEdited;
        private bool _staPasswordEdited;

        // Transport WIRELESS (WebSocket) - tạo cùng lúc với controller, dùng chung singleton
        // MlastroWebSocketService.Instance cho cả UI và driver TPPA (firmware chỉ cho 1 PC WS).
        private readonly MlastroWebSocketService _webSocketService;

        public PluginSettings Settings { get; }

        public PolarAlignmentDockVM PolarAlignmentVM => _polarAlignmentDockVM;

        public PolarAlignmentDataSourceMode[] AvailableDataSourceModes { get; } = Enum.GetValues<PolarAlignmentDataSourceMode>();

        public event PropertyChangedEventHandler? PropertyChanged;

        public string[] AvailableComPorts => string.IsNullOrWhiteSpace(Settings.ComPort)
            ? _serialConnectionService.AvailablePorts
            : _serialConnectionService.AvailablePorts.Contains(Settings.ComPort, StringComparer.OrdinalIgnoreCase)
                ? _serialConnectionService.AvailablePorts
                : _serialConnectionService.AvailablePorts.Concat(new[] { Settings.ComPort }).ToArray();

        /// <summary>
        /// COM ports with driver names for the dropdown (e.g. "COM4 - USB-SERIAL CH340").
        /// The currently selected port is always included even if it is momentarily
        /// not enumerated by the OS.
        /// </summary>
        public ComPortInfo[] AvailableComPortItems
        {
            get
            {
                var infos = _serialConnectionService.AvailableComPortInfos;
                if (string.IsNullOrWhiteSpace(Settings.ComPort))
                {
                    return infos;
                }

                if (infos.Any(i => string.Equals(i.PortName, Settings.ComPort, StringComparison.OrdinalIgnoreCase)))
                {
                    return infos;
                }

                return infos.Concat(new[] { new ComPortInfo(Settings.ComPort, string.Empty) }).ToArray();
            }
        }

        public string SerialConnectionStatus => IsWirelessTransport
            ? (string.IsNullOrWhiteSpace(_webSocketService.ConnectionStatus) ? "Disconnected" : _webSocketService.ConnectionStatus)
            : _serialConnectionService.ConnectionStatus;

        public string SerialHandshakeStatus => IsWirelessTransport
            ? _webSocketService.HandshakeStatus
            : _serialConnectionService.HandshakeStatus;

        public bool IsSerialConnected => _serialConnectionService.IsConnected || _webSocketService.IsConnected;

        /// <summary>TPPA đang giữ quyền điều khiển -> khoá tab CONFIGURATION.</summary>
        public bool IsExternalLocked => _serialConnectionService.IsExternalControlActive
                                        || (_webSocketService.IsConnected && _webSocketService.IsExternalControlActive);

        public bool IsExternalUnlocked => !IsExternalLocked;

        // ===== Chọn kiểu kết nối: Serial / Wireless =====
        public bool IsWirelessTransport => Settings.TransportMode == MlastroTransportMode.Wireless;

        public bool IsSerialTransport => !IsWirelessTransport;

        /// <summary>Hiện khối cài đặt Serial (COM port, data bits...) chỉ khi chọn Serial connection.</summary>
        public bool IsSerialSettingsVisible => IsSerialTransport;

        /// <summary>Hiện bảng Wireless (Địa chỉ / Kết nối / System log) chỉ khi chọn Wireless connection.</summary>
        public bool IsWirelessSettingsVisible => IsWirelessTransport;

        /// <summary>0 = Serial connection, 1 = Wireless connection (binding cho ComboBox ở tab CONNECTION).</summary>
        public int TransportModeIndex
        {
            get => IsWirelessTransport ? 1 : 0;
            set
            {
                var mode = value == 1 ? MlastroTransportMode.Wireless : MlastroTransportMode.Serial;
                if (Settings.TransportMode == mode)
                {
                    return;
                }

                // Đổi transport khi đang kết nối → ngắt transport cũ trước (mỗi lúc chỉ 1 transport giữ quyền).
                if (_serialConnectionService.IsConnected)
                {
                    _serialConnectionService.Disconnect();
                }
                if (_webSocketService.IsConnected)
                {
                    _webSocketService.Disconnect();
                }

                Settings.TransportMode = mode;
                RaiseTransportProperties();
            }
        }

        private void RaiseTransportProperties()
        {
            OnPropertyChanged(nameof(IsWirelessTransport));
            OnPropertyChanged(nameof(IsSerialTransport));
            OnPropertyChanged(nameof(IsSerialSettingsVisible));
            OnPropertyChanged(nameof(IsWirelessSettingsVisible));
            OnPropertyChanged(nameof(TransportModeIndex));
            RaiseConnectionProperties();
        }

        /// <summary>
        /// Làm mới binding transport trên UI thread: Connection type có thể bị TPPA đổi từ luồng nền
        /// (Connect chạy trong Task.Run) khi đường Wireless thất bại và phải fallback sang Serial.
        /// </summary>
        private void RaiseTransportPropertiesOnUiThread()
        {
            try
            {
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher != null && !dispatcher.CheckAccess())
                {
                    dispatcher.BeginInvoke(new Action(RaiseTransportProperties));
                }
                else
                {
                    RaiseTransportProperties();
                }
            }
            catch
            {
                RaiseTransportProperties();
            }
        }

        private void RaiseConnectionProperties()
        {
            OnPropertyChanged(nameof(IsSerialConnected));
            OnPropertyChanged(nameof(SerialConnectionStatus));
            OnPropertyChanged(nameof(SerialHandshakeStatus));
            OnPropertyChanged(nameof(SerialConnectButtonText));
            OnPropertyChanged(nameof(IsExternalLocked));
            OnPropertyChanged(nameof(IsExternalUnlocked));
        }

        public string SerialConnectButtonText => IsSerialConnected ? "Disconnect" : "Connect";

        public bool IsHexDisplay
        {
            get => _serialConnectionService.HexDisplay;
            set
            {
                if (_serialConnectionService.HexDisplay != value)
                {
                    _serialConnectionService.HexDisplay = value;
                    OnPropertyChanged();
                }
            }
        }

        public ObservableCollection<SerialTerminalEntry> SerialTerminalEntries => _serialConnectionService.TerminalEntries;

        /// <summary>
        /// System log kiểu Web UI (dùng ở chế độ Wireless): [thời gian] + nội dung, tô màu theo mức độ,
        /// dòng mới nhất lên trên, tối đa 50 dòng, KHÔNG có frame TX/RX thô.
        /// </summary>
        public ObservableCollection<SystemLogEntry> SystemLog => _webSocketService.SystemLog;

        public bool IsHexInputEnabled
        {
            get => _isHexInputEnabled;
            set
            {
                if (_isHexInputEnabled != value)
                {
                    _isHexInputEnabled = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(SerialInputMaxLength));
                }
            }
        }

        public int SerialInputMaxLength => IsHexInputEnabled ? 16 : 0;

        public string SerialTerminalInput
        {
            get => _serialTerminalInput;
            set
            {
                if (_serialTerminalInput != value)
                {
                    _serialTerminalInput = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsModifyMode
        {
            get => _isModifyMode;
            set
            {
                if (_isModifyMode != value)
                {
                    var wasModifyMode = _isModifyMode;
                    _isModifyMode = value;
                    _serialConnectionService.PauseTelemetryUpdates = value;
                    OnPropertyChanged();

                    if (value)
                    {
                        // Entering modify mode - save current settings snapshot
                        _savedSettingsSnapshot = _serialConnectionService.BuildConfigurationCommand(Settings);
                        Logger.Info("[MLAstro] Entering modify mode - settings snapshot saved");
                    }
                    else if (wasModifyMode)
                    {
                        // Exiting modify mode - check if settings changed
                        var currentSettings = _serialConnectionService.BuildConfigurationCommand(Settings);
                        if (currentSettings != _savedSettingsSnapshot)
                        {
                            Logger.Info("[MLAstro] Settings changed - saving to device");
                            SaveAllSettingsInternal();
                        }
                        else
                        {
                            Logger.Info("[MLAstro] No settings changed - skipping save");
                        }
                        _savedSettingsSnapshot = null;
                    }
                }
            }
        }

        public bool IsPauseQuery
        {
            get => SerialConnectionService.PauseQueryGlobal;
            set
            {
                if (SerialConnectionService.PauseQueryGlobal != value)
                {
                    SerialConnectionService.PauseQueryGlobal = value;
                    OnPropertyChanged();
                }
            }
        }

        public int HandshakeTimeoutMilliseconds
        {
            get => _serialConnectionService.HandshakeTimeoutMilliseconds;
            set => _serialConnectionService.HandshakeTimeoutMilliseconds = value;
        }

        public int PollingIntervalMilliseconds
        {
            get => _serialConnectionService.PollingIntervalMilliseconds;
            set => _serialConnectionService.PollingIntervalMilliseconds = value;
        }

        public string AutoReconnectStatus
        {
            get => _autoReconnectStatus;
            set
            {
                if (_autoReconnectStatus != value)
                {
                    _autoReconnectStatus = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsHandshakeSuccessful
        {
            get => _isHandshakeSuccessful;
            set
            {
                if (_isHandshakeSuccessful != value)
                {
                    _isHandshakeSuccessful = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool ShowApPassword
        {
            get => _showApPassword;
            private set
            {
                if (_showApPassword == value)
                {
                    return;
                }

                _showApPassword = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ApPassDisplay));
            }
        }

        public bool ShowStaPassword
        {
            get => _showStaPassword;
            private set
            {
                if (_showStaPassword == value)
                {
                    return;
                }

                _showStaPassword = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(WifiPassDisplay));
            }
        }

        public string ApPassDisplay
        {
            get => ShowApPassword ? Settings.ApPass : "********";
            set
            {
                if (ShowApPassword && value != Settings.ApPass)
                {
                    Settings.ApPass = value;
                    _apPasswordEdited = true;
                    OnPropertyChanged();
                }
            }
        }

        public string WifiPassDisplay
        {
            get => ShowStaPassword ? Settings.WifiPass : "********";
            set
            {
                if (ShowStaPassword && value != Settings.WifiPass)
                {
                    Settings.WifiPass = value;
                    _staPasswordEdited = true;
                    OnPropertyChanged();
                }
            }
        }

        public ICommand RefreshComPortsCommand { get; }

        public ICommand ToggleSerialConnectionCommand { get; }

        public ICommand SendSerialCommand { get; }

        public ICommand ClearSerialTerminalCommand { get; }

        public ICommand SaveAllSettingsCommand { get; }

        public ICommand ApplySettingsCommand { get; }

        public ICommand RebootCommand { get; }

        public ICommand ResetEsp32Command { get; }

        public ICommand ToggleShowApPasswordCommand { get; }

        public ICommand ToggleShowStaPasswordCommand { get; }

        public ICommand ClearSystemLogCommand { get; }

        public ICommand ExportSystemLogCommand { get; }

        public ICommand ResetErrorCommand { get; }

        // ===== External correction qua broker TPPA (tab SOFTWARE SETTING) =====

        private readonly IMessageBroker _messageBroker;
        private TppaBrokerClient? _tppaBrokerClient;
        private HardwareAligner? _hardwareAligner;
        private ExternalCorrectionRunner? _externalRunner;
        private const int BrokerLogMaxEntries = 50;

        /// <summary>Dòng log ngắn của tích hợp broker (mới nhất lên trên) để kiểm tra không cần mở log NINA.</summary>
        public ObservableCollection<BrokerLogEntry> BrokerLog { get; } = new();

        public ICommand StopExternalSessionCommand { get; }

        /// <summary>
        /// Switch bắt tay với TPPA. ON = announce capabilities + subscribe event + chạy runner.
        /// OFF = huỷ subscribe, bỏ qua mọi event; nếu đang có phiên thì gửi Cancel trước khi tắt.
        /// </summary>
        public bool TppaBrokerEnabled
        {
            get => Settings.TppaBrokerEnabled;
            set
            {
                if (Settings.TppaBrokerEnabled == value) { return; }

                if (value)
                {
                    Settings.TppaBrokerEnabled = true;
                    StartExternalCorrection();
                }
                else
                {
                    StopExternalCorrection();
                    Settings.TppaBrokerEnabled = false;
                }

                OnPropertyChanged();
                OnPropertyChanged(nameof(TppaBrokerStatusText));
            }
        }

        /// <summary>Trạng thái bắt tay với TPPA: off / searching / vX ready / incompatible.</summary>
        public string TppaBrokerStatusText => _tppaBrokerClient?.StatusText ?? "TPPA: off";

        /// <summary>Trạng thái vòng sửa hiện tại phía MLAstro.</summary>
        public string ExternalCorrectionStatusText => _externalRunner?.StatusText ?? "Idle";

        public ExternalAxisMode CorrectionAxisMode
        {
            get => Settings.CorrectionAxisMode;
            set
            {
                Settings.CorrectionAxisMode = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CorrectionAxisModeIndex));
            }
        }

        /// <summary>0 = Both (một lệnh ALIGN 2 trục), 1 = Auto (chỉ trục sai số lớn hơn).</summary>
        public int CorrectionAxisModeIndex
        {
            get => Settings.CorrectionAxisMode == ExternalAxisMode.Auto ? 1 : 0;
            set
            {
                CorrectionAxisMode = value == 1 ? ExternalAxisMode.Auto : ExternalAxisMode.Both;
            }
        }

        public double CorrectionSafetyFactor
        {
            get => Settings.CorrectionSafetyFactor;
            set
            {
                Settings.CorrectionSafetyFactor = Math.Max(0.05, Math.Min(1.0, value));
                OnPropertyChanged();
            }
        }

        public double CorrectionMaxStepArcMin
        {
            get => Settings.CorrectionMaxStepArcMin;
            set
            {
                Settings.CorrectionMaxStepArcMin = Math.Max(1, value);
                OnPropertyChanged();
            }
        }

        public bool CorrectionOvershootEnabled
        {
            get => Settings.CorrectionOvershootEnabled;
            set
            {
                Settings.CorrectionOvershootEnabled = value;
                OnPropertyChanged();
            }
        }

        public bool CorrectionOvershootUpEnabled
        {
            get => Settings.CorrectionOvershootUpEnabled;
            set
            {
                Settings.CorrectionOvershootUpEnabled = value;
                OnPropertyChanged();
            }
        }

        public bool CorrectionOvershootDownEnabled
        {
            get => Settings.CorrectionOvershootDownEnabled;
            set
            {
                Settings.CorrectionOvershootDownEnabled = value;
                OnPropertyChanged();
            }
        }

        public double CorrectionOvershootUpArcMin
        {
            get => Settings.CorrectionOvershootUpArcMin;
            set
            {
                Settings.CorrectionOvershootUpArcMin = Math.Max(0, value);
                OnPropertyChanged();
            }
        }

        public double CorrectionOvershootDownArcMin
        {
            get => Settings.CorrectionOvershootDownArcMin;
            set
            {
                Settings.CorrectionOvershootDownArcMin = Math.Max(0, value);
                OnPropertyChanged();
            }
        }

        public bool SoftwareReverseAzimuth
        {
            get => Settings.SoftwareReverseAzimuth;
            set
            {
                Settings.SoftwareReverseAzimuth = value;
                OnPropertyChanged();
            }
        }

        public bool SoftwareReverseAltitude
        {
            get => Settings.SoftwareReverseAltitude;
            set
            {
                Settings.SoftwareReverseAltitude = value;
                OnPropertyChanged();
            }
        }

        public double CorrectionAzBacklashArcMin
        {
            get => Settings.CorrectionAzBacklashArcMin;
            set
            {
                Settings.CorrectionAzBacklashArcMin = Math.Max(0, value);
                OnPropertyChanged();
            }
        }

        public int CorrectionTimeoutSec
        {
            get => Settings.CorrectionTimeoutSec;
            set
            {
                Settings.CorrectionTimeoutSec = Math.Max(60, value);
                OnPropertyChanged();
            }
        }

        public int CorrectionConsecutiveToFinish
        {
            get => Settings.CorrectionConsecutiveToFinish;
            set
            {
                Settings.CorrectionConsecutiveToFinish = Math.Max(1, value);
                OnPropertyChanged();
            }
        }

        /// <summary>Cảnh báo cấu hình overshoot (chỉ hiện khi overshoot đang bật và không hợp lệ).</summary>
        public string OvershootWarningText
        {
            get
            {
                if (!CorrectionOvershootEnabled) { return string.Empty; }
                var tolerance = _tppaBrokerClient?.Capabilities?.ToleranceArcMin ?? 0;
                if (tolerance <= 0) { return string.Empty; }

                var smallest = Math.Min(
                    CorrectionOvershootUpArcMin > 0 ? CorrectionOvershootUpArcMin : double.MaxValue,
                    CorrectionOvershootDownArcMin > 0 ? CorrectionOvershootDownArcMin : double.MaxValue);
                if (smallest == double.MaxValue) { return string.Empty; }

                return ExternalCorrectionEngine.IsOvershootAboveTolerance(smallest, tolerance)
                    ? string.Empty
                    : ExternalCorrectionEngine.OvershootWarning;
            }
        }

        public Visibility OvershootWarningVisibility =>
            string.IsNullOrEmpty(OvershootWarningText) ? Visibility.Collapsed : Visibility.Visible;

        /// <summary>Người dùng bấm Stop trên tab SOFTWARE SETTING: gửi Cancel và giữ motor dừng.</summary>
        private async Task StopExternalSessionAsync()
        {
            var runner = _externalRunner;
            if (runner == null || !runner.IsSessionRunning)
            {
                AppendBrokerLog("No external session is running.");
                return;
            }

            try
            {
                AppendBrokerLog("Stopping the external session on request.");
                await runner.AbortSessionAsync(TppaBrokerReason.UserStop).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }
        }

        /// <summary>
        /// STOP / FORCE STOP on the dock: the axes were stopped by hand, so the TPPA session has to end
        /// instead of waiting for measurements that will never be requested.
        /// </summary>
        private void OnManualStopRequested(object? sender, EventArgs e)
        {
            AbortExternalSessionForHardware(TppaBrokerReason.UserStop, "STOP on the MLAstro dock");
        }

        /// <summary>
        /// Ends a running TPPA session because the hardware side stopped on its own - a manual stop or a
        /// link that dropped - so TPPA does not keep waiting for a controller that cannot move anymore.
        /// </summary>
        private void AbortExternalSessionForHardware(string reason, string detail)
        {
            var runner = _externalRunner;
            if (runner == null || !runner.IsSessionRunning) { return; }

            AppendBrokerLog($"{detail}: asking TPPA to cancel the session ({reason}).");
            _ = runner.AbortSessionAsync(reason);
        }

        private void InitializeExternalCorrection(IMessageBroker messageBroker)
        {
            if (messageBroker == null) { return; }

            _tppaBrokerClient = new TppaBrokerClient(messageBroker, Settings);
            _hardwareAligner = new HardwareAligner(_serialConnectionService);
            _externalRunner = new ExternalCorrectionRunner(_tppaBrokerClient, _hardwareAligner, Settings);

            _tppaBrokerClient.StatusChanged += (_, __) => RaiseBrokerStatus();
            _tppaBrokerClient.Traffic += (_, e) => AppendBrokerLog(e.Direction, e.Detail);
            _externalRunner.StatusChanged += (_, __) => RaiseBrokerStatus(appendLog: true);
            // Trong phiên TPPA, chỉ controller ngoài được quay motor: khoá điều khiển tay/auto của dock.
            _externalRunner.SessionActiveChanged += (_, active) => SetDockAutomatedAdjustment(active);
            // STOP / FORCE STOP trên dock cũng phải kết thúc phiên TPPA: TPPA không được chờ một
            // controller vừa dừng trục bằng tay.
            if (_polarAlignmentDockVM != null)
            {
                _polarAlignmentDockVM.ManualStopRequested += OnManualStopRequested;
            }

            if (Settings.TppaBrokerEnabled)
            {
                StartExternalCorrection();
            }
            else
            {
                AppendBrokerLog("Broker off (switch disabled).");
            }
        }

        private void StartExternalCorrection()
        {
            _externalRunner?.Start();
            _tppaBrokerClient?.Start();
            AppendBrokerLog("Broker on: announcing capabilities to the polar alignment plugin.");
            RaiseBrokerStatus();
        }

        /// <summary>
        /// Khoá / mở khoá điều khiển tay của dock khi phiên TPPA bắt đầu / kết thúc. Chạy trên UI thread
        /// vì cờ này điều khiển enable/disable của các nút trên options page.
        /// </summary>
        private void SetDockAutomatedAdjustment(bool active)
        {
            try
            {
                var dispatcher = Application.Current?.Dispatcher;
                void apply()
                {
                    var vm = _polarAlignmentDockVM;
                    if (vm == null) { return; }
                    vm.IsAutomatedAdjustment = active;
                    AppendBrokerLog(active
                        ? "TPPA session owns the axes: manual controls locked."
                        : "TPPA session finished: manual controls unlocked.");
                }

                if (dispatcher != null && !dispatcher.CheckAccess())
                {
                    dispatcher.BeginInvoke(new Action(apply));
                }
                else
                {
                    apply();
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }
        }

        private void StopExternalCorrection()
        {
            try
            {
                if (_externalRunner?.IsSessionRunning == true)
                {
                    _externalRunner.AbortSessionAsync(TppaBrokerReason.UserStop).GetAwaiter().GetResult();
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }

            _externalRunner?.Stop();
            _tppaBrokerClient?.Stop();
            AppendBrokerLog("Broker off.");
            RaiseBrokerStatus();
        }

        private void RaiseBrokerStatus(bool appendLog = false)
        {
            try
            {
                var dispatcher = Application.Current?.Dispatcher;
                void update()
                {
                    OnPropertyChanged(nameof(TppaBrokerStatusText));
                    OnPropertyChanged(nameof(ExternalCorrectionStatusText));
                    OnPropertyChanged(nameof(OvershootWarningText));
                    OnPropertyChanged(nameof(OvershootWarningVisibility));
                    if (appendLog)
                    {
                        AppendBrokerLog(_externalRunner?.StatusText ?? string.Empty);
                    }
                }

                if (dispatcher != null && !dispatcher.CheckAccess())
                {
                    dispatcher.BeginInvoke(new Action(update));
                }
                else
                {
                    update();
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }
        }

        private void AppendBrokerLog(string message) => AppendBrokerLog(BrokerLogDirection.Notice, message);

        /// <summary>
        /// Thêm một dòng vào Broker log. Các dòng đến từ luồng broker được marshal sang UI thread vì
        /// collection này được bind vào options page.
        /// </summary>
        private void AppendBrokerLog(BrokerLogDirection direction, string message)
        {
            if (string.IsNullOrWhiteSpace(message)) { return; }

            try
            {
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher != null && !dispatcher.CheckAccess())
                {
                    dispatcher.BeginInvoke(new Action(() => InsertBrokerLog(direction, message)));
                    return;
                }

                InsertBrokerLog(direction, message);
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }
        }

        private void InsertBrokerLog(BrokerLogDirection direction, string message)
        {
            BrokerLog.Insert(0, new BrokerLogEntry(direction, message));
            while (BrokerLog.Count > BrokerLogMaxEntries)
            {
                BrokerLog.RemoveAt(BrokerLog.Count - 1);
            }
        }

        public MLAstroController(PluginSettings settings, SerialConnectionService serialConnectionService, PolarAlignmentDockVM polarAlignmentDockVM, IMessageBroker messageBroker)
        {
            Settings = settings;
            _serialConnectionService = serialConnectionService;
            _polarAlignmentDockVM = polarAlignmentDockVM;
            _messageBroker = messageBroker;
            _webSocketService = new MlastroWebSocketService(settings, serialConnectionService);

            RefreshComPortsCommand = new RelayCommand(RefreshComPorts);
            ToggleSerialConnectionCommand = new RelayCommand(ToggleSerialConnection);
            SendSerialCommand = new RelayCommand(SendSerial);
            ClearSerialTerminalCommand = new RelayCommand(ClearSerialTerminal);
            SaveAllSettingsCommand = new RelayCommand(SaveAllSettings);
            ApplySettingsCommand = new RelayCommand(ApplySettings);
            RebootCommand = new RelayCommand(ResetEsp32);
            ResetEsp32Command = new RelayCommand(ResetEsp32);
            ToggleShowApPasswordCommand = new RelayCommand(ToggleShowApPassword);
            ToggleShowStaPasswordCommand = new RelayCommand(ToggleShowStaPassword);
            ClearSystemLogCommand = new RelayCommand(_webSocketService.ClearSystemLog);
            ExportSystemLogCommand = new RelayCommand(ExportSystemLog);
            ResetErrorCommand = new RelayCommand(ResetError);
            StopExternalSessionCommand = new RelayCommand(async () => await StopExternalSessionAsync());

            Settings.PropertyChanged += OnSettingsPropertyChanged;
            _serialConnectionService.PropertyChanged += OnSerialConnectionServicePropertyChanged;
            _serialConnectionService.AddExternalControlListener(_ =>
            {
                OnPropertyChanged(nameof(IsExternalLocked));
                OnPropertyChanged(nameof(IsExternalUnlocked));
                OnPropertyChanged(nameof(IsPauseQuery));
            });
            // Theo dõi PauseQueryGlobal đổi (TPPA mượn/trả cổng hay gạt tay) để checkbox hiện đúng trạng thái.
            _onPauseQueryChanged = paused => OnPropertyChanged(nameof(IsPauseQuery));
            SerialConnectionService.PauseQueryChanged += _onPauseQueryChanged;

            // Transport wireless đổi trạng thái ở luồng nền → marshal về UI thread trước khi báo binding.
            _webSocketService.PropertyChanged += (_, __) =>
            {
                try
                {
                    var dispatcher = Application.Current?.Dispatcher;
                    if (dispatcher != null) dispatcher.Invoke(RaiseConnectionProperties);
                    else RaiseConnectionProperties();
                }
                catch { }
            };
            RefreshComPorts();

            // Tích hợp broker với TPPA: announce capabilities + chạy vòng sửa tự động (nếu switch ON).
            InitializeExternalCorrection(messageBroker);

            // Hook into application exit to ensure cleanup - must run on UI thread
            if (Application.Current != null)
            {
                try
                {
                    Application.Current.Dispatcher.Invoke(() => Application.Current.Exit += OnApplicationExit);
                }
                catch { }
            }

            // Setup FileSystemWatcher to detect when plugin is being uninstalled
            // NINA moves plugin folder to DeletionFolder when user clicks Uninstall
            SetupPluginFolderWatcher();

            try
            {
                if (Application.Current != null)
                {
                    try
                    {
                        // Create and add ResourceDictionary on UI thread because ResourceDictionary/DependencyObject
                        // must be owned by the UI thread's Dispatcher.
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            _pluginResourceDictionary = new ResourceDictionary
                            {
                                Source = new Uri("pack://application:,,,/NINA.Plugins.MLAstroRPA;component/MLAstroRPA-navigation/Dockview/Dockable.xaml", UriKind.Absolute)
                            };
                            Application.Current.Resources.MergedDictionaries.Add(_pluginResourceDictionary);
                        });
                    }
                    catch { }
                }

                var iconLocatorType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => {
                        try { return a.GetTypes(); } catch { return Type.EmptyTypes; }
                    })
                    .FirstOrDefault(t => t.Name == "IconLocator");

                if (iconLocatorType != null)
                {
                    var registerMethod = iconLocatorType.GetMethod("Register", new[] { typeof(Uri) });
                    if (registerMethod != null)
                    {
                        var uri = new Uri("pack://application:,,,/NINA.Plugins.MLAstroRPA;component/MLAstroRPA-navigation/Resources/MLAstroIcons.xaml", UriKind.Absolute);
                        try
                        {
                            // Ensure any UI-related registration runs on the UI thread
                            Application.Current?.Dispatcher?.Invoke(() => registerMethod.Invoke(null, new object[] { uri }));
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        private void OnApplicationExit(object sender, ExitEventArgs e)
        {
            Logger.Info("[MLAstro] Application exiting, disposing plugin resources...");
            Dispose();
        }

        /// <summary>
        /// Warns when a second MLAstro plugin DLL sits next to this one in the Plugins folder. Both may stay
        /// installed, but each plugin opens its own COM port, so only the instance that owns the port can drive
        /// the hardware.
        /// </summary>
        private void WarnIfDuplicatePluginInstalled(string pluginFolder)
        {
            try
            {
                if (string.IsNullOrEmpty(pluginFolder)) { return; }

                var parent = Path.GetDirectoryName(pluginFolder);
                if (string.IsNullOrEmpty(parent)) { return; }

                var duplicates = new[]
                {
                    Path.Combine(parent, "MLAstroRPA-TPPA", "NINA.Plugins.MLAstroRPA_TPPA.dll"),
                    Path.Combine(parent, "MLAstroRPA_TPPA", "NINA.Plugins.MLAstroRPA_TPPA.dll")
                };

                foreach (var duplicate in duplicates)
                {
                    if (!File.Exists(duplicate)) { continue; }

                    Logger.Warning($"[MLAstro] Another MLAstro plugin is installed next to this one: {duplicate}. " +
                                   "Both may stay installed, but each plugin opens its own COM port: connect and send " +
                                   "commands from the same plugin (commands sent by a plugin that does not own the " +
                                   "port are dropped).");

                    return;
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }
        }

        /// <summary>
        /// Setup a FileSystemWatcher to detect when our plugin folder is being moved/deleted.
        /// NINA moves plugin folder to DeletionFolder when user clicks Uninstall.
        /// This allows us to close the dockable before the uninstall completes.
        /// </summary>
        private void SetupPluginFolderWatcher()
        {
            try
            {
                // Get the plugin assembly's directory
                // Plugin is at: %LOCALAPPDATA%\NINA\Plugins\3.0.0\MLAstroRPA
                var assemblyLocation = GetType().Assembly.Location;
                var pluginFolder = Path.GetDirectoryName(assemblyLocation);

                Logger.Info($"[MLAstro] Plugin assembly location: {assemblyLocation}");
                Logger.Info($"[MLAstro] Plugin folder: {pluginFolder}");

                WarnIfDuplicatePluginInstalled(pluginFolder);

                // Only watch OUR plugin folder for file deletions
                // This ensures we only trigger when MLAstro RPA plugin is being uninstalled
                // NOT when other plugins are installed/uninstalled
                if (!string.IsNullOrEmpty(pluginFolder) && Directory.Exists(pluginFolder))
                {
                    // Use a flag to only trigger notification once
                    bool pluginFolderNotificationShown = false;

                    _pluginFolderWatcher = new FileSystemWatcher(pluginFolder)
                    {
                        NotifyFilter = NotifyFilters.FileName,
                        Filter = "*.dll",
                        IncludeSubdirectories = false,
                        EnableRaisingEvents = true
                    };

                    _pluginFolderWatcher.Deleted += (s, e) =>
                    {
                        Logger.Info($"[MLAstro] File deleted/moved from plugin folder: {e.Name}");
                        // Only trigger on OUR plugin's DLL deletion
                        if (e.Name?.Contains("MLAstro", StringComparison.OrdinalIgnoreCase) == true ||
                            e.Name?.Equals("System.IO.Ports.dll", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            if (!pluginFolderNotificationShown)
                            {
                                pluginFolderNotificationShown = true;
                                Logger.Info($"[MLAstro] MLAstro plugin DLL moved: {e.Name} - triggering uninstall cleanup");
                                OnPluginUninstalling();
                            }
                        }
                    };

                    Logger.Info("[MLAstro] Plugin folder watcher setup complete");
                }
                else
                {
                    Logger.Warning("[MLAstro] Plugin folder not found - cannot setup watcher");
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"[MLAstro] Failed to setup plugin folder watcher: {ex.Message}");
            }
        }

        /// <summary>
        /// Called when the plugin is being uninstalled (folder moved to DeletionFolder).
        /// Shows a notification to user that NINA restart is required.
        /// </summary>
        private void OnPluginUninstalling()
        {
            try
            {
                Logger.Info("[MLAstro] Plugin uninstalling detected");

                // Show notification to user - specific to MLAstro RPA plugin
                Notification.ShowWarning(
                    "MLAstro RPA: Plugin is being uninstalled. Please RESTART NINA to complete the removal and close the control panel.",
                    TimeSpan.FromMinutes(5));

                // Must run on UI thread
                Application.Current?.Dispatcher?.Invoke(() =>
                {
                    try
                    {
                        var dockableVM = _polarAlignmentDockVM;
                        if (dockableVM != null)
                        {
                            Logger.Info("[MLAstro] Closing dockable panel...");
                            dockableVM.IsVisible = false;
                            dockableVM.IsClosed = true;
                            dockableVM.Dispose();
                            Logger.Info("[MLAstro] Dockable panel closed successfully");
                        }

                        // Disconnect serial
                        if (_serialConnectionService?.IsConnected == true)
                        {
                            _serialConnectionService.Disconnect();
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"[MLAstro] Error closing dockable on uninstall: {ex.Message}");
                    }
                });

                // Try to delete the plugin folder after a short delay (allow NINA to finish moving files)
                Task.Run(async () =>
                {
                    try
                    {
                        // Wait for NINA to finish moving files
                        await Task.Delay(2000);

                        var assemblyLocation = GetType().Assembly.Location;
                        var pluginFolder = Path.GetDirectoryName(assemblyLocation);

                        if (!string.IsNullOrEmpty(pluginFolder) && Directory.Exists(pluginFolder))
                        {
                            Logger.Info($"[MLAstro] Attempting to delete plugin folder: {pluginFolder}");

                            // Try to delete remaining files first
                            foreach (var file in Directory.GetFiles(pluginFolder, "*.*", SearchOption.AllDirectories))
                            {
                                try
                                {
                                    File.Delete(file);
                                    Logger.Info($"[MLAstro] Deleted file: {file}");
                                }
                                catch (Exception ex)
                                {
                                    Logger.Warning($"[MLAstro] Could not delete file {file}: {ex.Message}");
                                }
                            }

                            // Try to delete empty subdirectories
                            foreach (var dir in Directory.GetDirectories(pluginFolder, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
                            {
                                try
                                {
                                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                                    {
                                        Directory.Delete(dir);
                                        Logger.Info($"[MLAstro] Deleted empty directory: {dir}");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Logger.Warning($"[MLAstro] Could not delete directory {dir}: {ex.Message}");
                                }
                            }

                            // Try to delete the plugin folder itself
                            try
                            {
                                if (!Directory.EnumerateFileSystemEntries(pluginFolder).Any())
                                {
                                    Directory.Delete(pluginFolder);
                                    Logger.Info($"[MLAstro] Plugin folder deleted successfully");
                                }
                                else
                                {
                                    Logger.Info($"[MLAstro] Plugin folder not empty, will be cleaned up on NINA restart");
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Warning($"[MLAstro] Could not delete plugin folder: {ex.Message}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"[MLAstro] Error during plugin folder cleanup: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.Warning($"[MLAstro] Error in OnPluginUninstalling: {ex.Message}");
            }
        }

        private void RefreshComPorts()
        {
            _serialConnectionService.RefreshPorts();
            OnPropertyChanged(nameof(AvailableComPorts));
            OnPropertyChanged(nameof(AvailableComPortItems));
        }

        private async void ToggleSerialConnection()
        {
            if (IsWirelessTransport)
            {
                if (_webSocketService.IsConnected)
                {
                    _webSocketService.Disconnect();
                }
                else
                {
                    // Báo RÕ khi kết nối không dây thất bại (mDNS không resolve / sai địa chỉ) thay vì
                    // im lặng để người dùng tưởng đã kết nối được.
                    if (!await _webSocketService.ConnectAsync())
                    {
                        var wirelessReason = _webSocketService.ConnectionStatus;
                        Logger.Warning($"[MLAstro] Wireless connect failed: {wirelessReason}");
                        Notification.ShowWarning($"Wireless connection failed: {wirelessReason}");
                    }
                }
                RaiseConnectionProperties();
                return;
            }

            if (IsSerialConnected)
            {
                _serialConnectionService.Disconnect();
                return;
            }

            RefreshComPorts();
            await _serialConnectionService.ConnectAsync(Settings.ComPort, SerialBaudRate);
        }

        private void SendSerial()
        {
            var input = SerialTerminalInput;

            if (IsWirelessTransport)
            {
                var line = input.EndsWith('\n') ? input : input + "\n";
                if (_webSocketService.Send(line))
                {
                    SerialTerminalInput = string.Empty;
                }
                return;
            }

            var sent = IsHexInputEnabled
                ? _serialConnectionService.SendHex(input)
                : _serialConnectionService.Send(input.EndsWith('\n') ? input : input + "\n");

            if (sent)
            {
                SerialTerminalInput = string.Empty;
            }
        }

        private void ToggleShowApPassword()
        {
            ShowApPassword = !ShowApPassword;

            if (ShowApPassword)
            {
                _serialConnectionService.QueryApPassword();
            }
        }

        private void ToggleShowStaPassword()
        {
            ShowStaPassword = !ShowStaPassword;

            if (ShowStaPassword)
            {
                _serialConnectionService.QueryStaPassword();
            }
        }

        private void ClearSerialTerminal()
        {
            _serialConnectionService.ClearTerminal();
        }

        /// <summary>Xuất System log ra file CSV (giống nút Export CSV của Web UI).</summary>
        private void ExportSystemLog()
        {
            try
            {
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Export System log",
                    FileName = $"MLAstroRPA-systemlog-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
                    Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*"
                };

                if (dialog.ShowDialog() != true)
                {
                    return;
                }

                File.WriteAllText(dialog.FileName, _webSocketService.BuildSystemLogCsv());
                Logger.Info($"[MLAstro] System log exported to {dialog.FileName}");
            }
            catch (Exception ex)
            {
                Logger.Error($"[MLAstro] Export system log failed: {ex.Message}");
            }
        }

        /// <summary>Gửi ReER:1 (resetError) — dùng chung facade nên chạy được cả Serial lẫn Wireless.</summary>
        private void ResetError()
        {
            if (!IsSerialConnected)
            {
                return;
            }

            if (!_serialConnectionService.Send("ReER:1\n"))
            {
                Logger.Warning("[MLAstro] Reset error command failed");
            }
        }

        private async void ResetEsp32()
        {
            if (IsWirelessTransport)
            {
                if (!_webSocketService.ResetEsp32())
                {
                    return;
                }

                // PHẢI ngắt phiên hiện tại trước khi kết nối lại: `ConnectAsync()` bỏ qua ngay khi
                // `IsConnected == true`, nên nếu không Disconnect thì bước "reconnect" không làm gì cả
                // (UI vẫn báo Connected trong khi socket đã chết theo device đang reboot).
                _webSocketService.Disconnect();
                await AutoReconnectAsync(3);
                return;
            }

            if (!_serialConnectionService.ResetEsp32())
            {
                return;
            }

            await AutoReconnectAsync(3);
        }

        private async void SaveAllSettingsInternal()
        {
            if (!IsSerialConnected)
            {
                return;
            }

            // ---- WIRELESS: lưu cấu hình qua WebSocket API (saveConfig + reboot) ----
            // Cùng một dòng lệnh text như đường Serial: translator dịch sang saveConfig
            // (limits/motor/backlash + wifi_ap/wifi) rồi gửi reboot. Password của AP/STA trước đây
            // bị bỏ lại phía Serial nên đổi password bằng Wireless không có tác dụng.
            if (IsWirelessTransport)
            {
                try
                {
                    var wirelessConfig = _serialConnectionService.BuildConfigurationCommand(Settings);
                    // CHỈ gửi password khi có giá trị: binding WPF (UpdateSourceTrigger=PropertyChanged)
                    // có thể ghi giá trị trung gian rỗng trong lúc người dùng đang gõ lại, và gửi
                    // "STAp:"/"APpa:" rỗng sẽ XOÁ mật khẩu trên thiết bị (→ STA fail reason 15).
                    if (_apPasswordEdited && !string.IsNullOrEmpty(Settings.ApPass))
                    {
                        wirelessConfig = $"APpa:{Settings.ApPass}," + wirelessConfig;
                    }
                    if (_staPasswordEdited && !string.IsNullOrEmpty(Settings.WifiPass))
                    {
                        wirelessConfig = $"STAp:{Settings.WifiPass}," + wirelessConfig;
                    }

                    var ok = await _webSocketService.SendCommandAndAwaitOkAsync(wirelessConfig);
                    if (!ok)
                    {
                        Logger.Warning("[MLAstro] saveConfig was not acknowledged over wireless");
                    }
                    _apPasswordEdited = false;
                    _staPasswordEdited = false;

                    _webSocketService.Disconnect();
                    await AutoReconnectAsync(3);
                }
                catch (Exception ex)
                {
                    Logger.Error($"[MLAstro] Save settings (wireless) failed: {ex.Message}");
                }
                return;
            }

            try
            {
                // Không gửi giá trị rỗng (xem ghi chú ở nhánh wireless): giá trị rỗng sẽ xoá mật khẩu
                // đang lưu trên thiết bị, khiến STA không kết nối được nữa (reason 15).
                if (_apPasswordEdited && !string.IsNullOrEmpty(Settings.ApPass) &&
                    !await _serialConnectionService.SendCommandAndAwaitOkAsync($"APpa:{Settings.ApPass}\n"))
                {
                    Logger.Warning("[MLAstro] AP password update was not acknowledged");
                    return;
                }
                _apPasswordEdited = false;

                if (_staPasswordEdited && !string.IsNullOrEmpty(Settings.WifiPass) &&
                    !await _serialConnectionService.SendCommandAndAwaitOkAsync($"STAp:{Settings.WifiPass}\n"))
                {
                    Logger.Warning("[MLAstro] Station password update was not acknowledged");
                    return;
                }
                _staPasswordEdited = false;

                // Password updates are sent separately above; send the remaining configuration last.
                var configCommand = _serialConnectionService.BuildConfigurationCommand(Settings);

                // Chuẩn bị chờ marker TRƯỚC khi gửi: firmware in "All Setting Saved" ngay sau khi ghi
                // FRAM xong, nếu arm sau khi gửi thì có thể lỡ marker.
                _serialConnectionService.ArmAllSettingsSavedWaiter();

                // Send to device
                var sent = _serialConnectionService.Send(configCommand);
                if (!sent)
                {
                    Logger.Warning("[MLAstro] Failed to send configuration command");
                    return;
                }

                // CHỈ coi là lưu thành công khi nhận được marker "All Setting Saved" từ thiết bị.
                var saved = await _serialConnectionService.WaitForAllSettingsSavedAsync(6000);
                if (!saved)
                {
                    Logger.Error("[MLAstro] Save NOT confirmed: no 'All Setting Saved' marker from the device within 6s - ESP32 is NOT reset (settings may be unsaved).");
                    return;
                }

                Logger.Info("[MLAstro] FRAM save confirmed ('All Setting Saved'). Resetting ESP32 via DTR/RTS...");

                // Firmware KHÔNG tự reboot (xem Serial-protocol.md) — PC phải reset ESP qua EN pin.
                _serialConnectionService.ResetEsp32();

                // Disconnect
                _serialConnectionService.Disconnect();

                // Start countdown and auto-reconnect
                await AutoReconnectAsync();
            }
            catch (Exception ex)
            {
                Logger.Error($"[MLAstro] Save settings failed: {ex.Message}");
            }
        }

        private void ApplySettings()
        {
            if (!IsSerialConnected)
            {
                Logger.Warning("[MLAstro] Apply settings skipped - not connected");
                return;
            }

            if (IsWirelessTransport)
            {
                var wirelessConfig = _serialConnectionService.BuildConfigurationCommand(Settings, includeSaveAndReboot: false);
                _ = _webSocketService.SendCommandAndAwaitOkAsync(wirelessConfig).ContinueWith(t =>
                {
                    if (t.Result)
                    {
                        Logger.Info("[MLAstro] Settings applied to device memory (wireless)");
                    }
                });
                ResumeSettingsSync();
                return;
            }

            var configCommand = _serialConnectionService.BuildConfigurationCommand(Settings, includeSaveAndReboot: false);
            var sent = _serialConnectionService.Send(configCommand);
            if (sent)
            {
                Logger.Info("[MLAstro] Settings applied to device memory");
                ResumeSettingsSync();
            }
        }

        private void ResumeSettingsSync()
        {
            if (_hasUserSettingsEdits)
            {
                _hasUserSettingsEdits = false;
                _serialConnectionService.SuspendSettingsSync = false;
                Logger.Info("[MLAstro] Telemetry settings sync resumed after apply/save");
            }
        }

        private void SaveAllSettings()
        {
            SaveAllSettingsInternal();
        }

        private async System.Threading.Tasks.Task AutoReconnectAsync(int countdownSeconds = 5)
        {
            try
            {
                // ---- WIRELESS: không có COM port để liệt kê lại, chỉ kết nối lại WebSocket ----
                if (IsWirelessTransport)
                {
                    for (int i = countdownSeconds; i > 0; i--)
                    {
                        AutoReconnectStatus = $"Reconnecting in {i}s...";
                        await System.Threading.Tasks.Task.Delay(1000);
                    }

                    // Chỉ thử lại trong WirelessReconnectWindowSeconds (30 s, đủ cho device reboot) rồi báo lỗi.
                    var wirelessDeadline = DateTime.UtcNow.AddSeconds(WirelessReconnectWindowSeconds);

                    for (int attempt = 0; ; attempt++)
                    {
                        AutoReconnectStatus = $"Connecting... (attempt {attempt + 1})";
                        if (await _webSocketService.ConnectAsync())
                        {
                            AutoReconnectStatus = "Connected";
                            await System.Threading.Tasks.Task.Delay(2000);
                            AutoReconnectStatus = string.Empty;
                            RaiseConnectionProperties();
                            return;
                        }

                        // Hết thời gian thử lại (30 s, đủ cho reboot) → báo lỗi kèm lý do, KHÔNG thử vô hạn.
                        if (DateTime.UtcNow >= wirelessDeadline)
                        {
                            break;
                        }

                        await System.Threading.Tasks.Task.Delay(500);
                    }

                    AutoReconnectStatus = string.Empty;
                    var reconnectReason = _webSocketService.ConnectionStatus;
                    Logger.Warning($"[MLAstro] Wireless reconnect failed after {WirelessReconnectWindowSeconds}s: {reconnectReason}");
                    ShowConnectionError(reconnectReason);
                    return;
                }

                // Countdown before first reconnect attempt (firmware needs time to reboot)
                for (int i = countdownSeconds; i > 0; i--)
                {
                    AutoReconnectStatus = $"Reconnecting in {i}s...";
                    await System.Threading.Tasks.Task.Delay(1000);
                }

                var connected = false;
                var handshakeSuccess = false;

                // Firmware reboot takes ~5s and the COM port may re-enumerate, so retry
                // connect + handshake several times instead of failing after a single attempt.
                const int maxConnectAttempts = 10;
                const int maxHandshakeAttempts = 5;

                for (int attempt = 0; attempt < maxConnectAttempts; attempt++)
                {
                    if (attempt > 0)
                    {
                        AutoReconnectStatus = $"Waiting for device... (retry {attempt + 1}/{maxConnectAttempts})";
                        await System.Threading.Tasks.Task.Delay(1000);
                    }

                    RefreshComPorts();

                    AutoReconnectStatus = $"Connecting... (attempt {attempt + 1}/{maxConnectAttempts})";
                    connected = await _serialConnectionService.ConnectAsync(Settings.ComPort, SerialBaudRate);
                    if (!connected)
                    {
                        continue;
                    }

                    // Retry handshake until the firmware is ready to answer
                    handshakeSuccess = false;
                    for (int h = 0; h < maxHandshakeAttempts; h++)
                    {
                        if (_serialConnectionService.HandshakeStatus == "OK!")
                        {
                            handshakeSuccess = true;
                            break;
                        }

                        var ok = await _serialConnectionService.SendHandshakeAsync();
                        if (ok)
                        {
                            handshakeSuccess = true;
                            break;
                        }

                        await System.Threading.Tasks.Task.Delay(500);
                    }

                    if (handshakeSuccess)
                    {
                        break;
                    }

                    // Firmware not ready on this port yet - disconnect and retry
                    _serialConnectionService.Disconnect();
                }

                if (connected && handshakeSuccess)
                {
                    AutoReconnectStatus = "Connected";
                    await System.Threading.Tasks.Task.Delay(2000);
                    AutoReconnectStatus = string.Empty;
                }
                else
                {
                    AutoReconnectStatus = string.Empty;
                    ShowConnectionError();
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[MLAstro] Auto reconnect failed: {ex.Message}");
                AutoReconnectStatus = "Error";
                ShowConnectionError();
            }
        }

        private void ShowConnectionError(string detail = null)
        {
            try
            {
                if (Application.Current?.Dispatcher != null)
                {
                    var message = "CANNOT CONNECTED TO MLAstroRPA HARDWARE";
                    if (!string.IsNullOrWhiteSpace(detail))
                    {
                        message += $"\n\n{detail}";
                    }

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        MessageBox.Show(
                            message,
                            "Connection Error",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);

                        AutoReconnectStatus = string.Empty;
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[MLAstro] Failed to show error dialog: {ex.Message}");
            }
        }

        private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // Detect user edits (not telemetry-driven) while connected and pause telemetry settings
            // sync so polling does not overwrite values the user is typing before Apply.
            // HandshakeTimeout/PollingInterval are NOT synced from telemetry, so they don't count as dirty.
            if (_serialConnectionService.IsConnected
                && !_serialConnectionService.IsApplyingTelemetrySettings
                && !_hasUserSettingsEdits
                && e.PropertyName != nameof(PluginSettings.HandshakeTimeoutMilliseconds)
                && e.PropertyName != nameof(PluginSettings.PollingIntervalMilliseconds)
                && e.PropertyName != nameof(PluginSettings.ComPort)) // ComPort đổi do mở kết nối (không phải user sửa config)
            {
                _hasUserSettingsEdits = true;
                _serialConnectionService.SuspendSettingsSync = true;
                Logger.Info("[MLAstro] User settings edit detected - telemetry settings sync suspended");
            }

            if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(PluginSettings.TransportMode))
            {
                // TPPA có thể TỰ chuyển Connection type về Serial khi đường Wireless thất bại → UI
                // (ComboBox + khối cài đặt Serial/Wireless) phải đổi theo, kể cả khi sự kiện đến từ
                // luồng nền (Connect của TPPA chạy trong Task.Run).
                RaiseTransportPropertiesOnUiThread();
            }

            if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(PluginSettings.ComPort))
            {
                OnPropertyChanged(nameof(AvailableComPorts));
                OnPropertyChanged(nameof(AvailableComPortItems));
            }

            if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(PluginSettings.ApPass))
            {
                OnPropertyChanged(nameof(ApPassDisplay));
            }

            if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(PluginSettings.WifiPass))
            {
                OnPropertyChanged(nameof(WifiPassDisplay));
            }
        }

        private void OnSerialConnectionServicePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.PropertyName)
                || e.PropertyName == nameof(SerialConnectionService.AvailablePorts))
            {
                OnPropertyChanged(nameof(AvailableComPorts));
            }

            if (string.IsNullOrEmpty(e.PropertyName)
                || e.PropertyName == nameof(SerialConnectionService.AvailableComPortInfos))
            {
                OnPropertyChanged(nameof(AvailableComPortItems));
            }

            if (string.IsNullOrEmpty(e.PropertyName)
                || e.PropertyName == nameof(SerialConnectionService.ConnectionStatus))
            {
                OnPropertyChanged(nameof(SerialConnectionStatus));
            }

            if (string.IsNullOrEmpty(e.PropertyName)
                || e.PropertyName == nameof(SerialConnectionService.IsConnected))
            {
                // Mất kết nối firmware giữa phiên TPPA: ta không còn điều khiển được trục nữa, nên
                // báo TPPA cancel thay vì để nó chờ thêm measurement.
                if (!_serialConnectionService.IsConnected)
                {
                    AbortExternalSessionForHardware(TppaBrokerReason.Disconnect, "Firmware link lost");
                }
            }

            if (string.IsNullOrEmpty(e.PropertyName)
                || e.PropertyName == nameof(SerialConnectionService.HandshakeStatus))
            {
                OnPropertyChanged(nameof(SerialHandshakeStatus));

                // Update IsHandshakeSuccessful based on HandshakeStatus
                var isSuccess = _serialConnectionService.HandshakeStatus == "OK!";
                IsHandshakeSuccessful = isSuccess;

                // Reset IsModifyMode when handshake fails
                if (!isSuccess && IsModifyMode)
                {
                    Logger.Info("[MLAstro] Handshake failed - resetting modify mode");
                    _isModifyMode = false; // Direct set to avoid triggering save
                    _serialConnectionService.PauseTelemetryUpdates = false;
                    _savedSettingsSnapshot = null;
                    OnPropertyChanged(nameof(IsModifyMode));
                }

                // Reset user-edit suspension when handshake fails
                if (!isSuccess)
                {
                    _hasUserSettingsEdits = false;
                    _serialConnectionService.SuspendSettingsSync = false;
                }
            }

            if (string.IsNullOrEmpty(e.PropertyName)
                || e.PropertyName == nameof(SerialConnectionService.IsConnected))
            {
                OnPropertyChanged(nameof(IsSerialConnected));
                OnPropertyChanged(nameof(SerialConnectButtonText));

                // Reset states when disconnected
                if (!_serialConnectionService.IsConnected)
                {
                    IsHandshakeSuccessful = false;
                    ShowApPassword = false;
                    ShowStaPassword = false;

                    // Reset IsModifyMode when disconnected
                    if (IsModifyMode)
                    {
                        Logger.Info("[MLAstro] Disconnected - resetting modify mode");
                        _isModifyMode = false; // Direct set to avoid triggering save
                        _serialConnectionService.PauseTelemetryUpdates = false;
                        _savedSettingsSnapshot = null;
                        OnPropertyChanged(nameof(IsModifyMode));
                    }

                    // Reset user-edit suspension when disconnected
                    _hasUserSettingsEdits = false;
                    _serialConnectionService.SuspendSettingsSync = false;
                }
            }

            if (string.IsNullOrEmpty(e.PropertyName)
                || e.PropertyName == nameof(SerialConnectionService.HandshakeTimeoutMilliseconds))
            {
                OnPropertyChanged(nameof(HandshakeTimeoutMilliseconds));
            }

            if (string.IsNullOrEmpty(e.PropertyName)
                || e.PropertyName == nameof(SerialConnectionService.PollingIntervalMilliseconds))
            {
                OnPropertyChanged(nameof(PollingIntervalMilliseconds));
            }

            if (string.IsNullOrEmpty(e.PropertyName)
                || e.PropertyName == nameof(SerialConnectionService.HexDisplay))
            {
                OnPropertyChanged(nameof(IsHexDisplay));
            }
        }

        private void OnPropertyChanged([CallerMemberName] string propertyName = null!)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #region IDisposable

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (disposing)
            {
                Logger.Info("[MLAstro] MLAstroController disposing...");

                // Tích hợp broker: ngắt phiên đang chạy, huỷ subscribe rồi giải phóng.
                try
                {
                    _externalRunner?.Dispose();
                    _hardwareAligner?.Dispose();
                    _tppaBrokerClient?.Dispose();
                }
                catch (Exception ex)
                {
                    Logger.Error(ex);
                }

                // Dispose the polar alignment view model first
                try
                {
                    var dockableVM = _polarAlignmentDockVM;
                    Logger.Info($"[MLAstro] PolarAlignmentDockVM is {(dockableVM != null ? "not null" : "NULL")}");

                    if (dockableVM != null)
                    {
                        Logger.Info("[MLAstro] Disposing polar alignment VM - setting IsVisible=false, IsClosed=true");
                        // Hide the view first
                        dockableVM.IsVisible = false;
                        // Then mark it as closed
                        dockableVM.IsClosed = true;
                        // Dispose resources
                        dockableVM.Dispose();
                        Logger.Info("[MLAstro] Polar alignment VM hidden, closed and disposed");
                    }
                    else
                    {
                        Logger.Warning("[MLAstro] PolarAlignmentDockVM is null - cannot dispose polar alignment VM");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"[MLAstro] Failed to dispose polar alignment VM: {ex.Message}");
                }

                // Remove ResourceDictionary from Application to release assembly reference
                try
                {
                    if (Application.Current != null && _pluginResourceDictionary != null)
                    {
                        Application.Current.Resources.MergedDictionaries.Remove(_pluginResourceDictionary);
                        _pluginResourceDictionary = null;
                        Logger.Info("[MLAstro] ResourceDictionary removed from Application");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"[MLAstro] Failed to remove ResourceDictionary: {ex.Message}");
                }

                // Unsubscribe from application exit event
                if (Application.Current != null)
                {
                    Application.Current.Exit -= OnApplicationExit;
                }

                // Dispose FileSystemWatcher
                if (_pluginFolderWatcher != null)
                {
                    _pluginFolderWatcher.EnableRaisingEvents = false;
                    _pluginFolderWatcher.Dispose();
                    _pluginFolderWatcher = null;
                    Logger.Info("[MLAstro] Plugin folder watcher disposed");
                }

                // Unsubscribe from settings events
                if (Settings != null)
                {
                    Settings.PropertyChanged -= OnSettingsPropertyChanged;
                }

                // Unsubscribe from pause-query change event
                if (_onPauseQueryChanged != null)
                {
                    SerialConnectionService.PauseQueryChanged -= _onPauseQueryChanged;
                }

                // Unsubscribe from serial connection service events
                if (_serialConnectionService != null)
                {
                    _serialConnectionService.PropertyChanged -= OnSerialConnectionServicePropertyChanged;
                    if (_polarAlignmentDockVM != null)
                    {
                        _polarAlignmentDockVM.ManualStopRequested -= OnManualStopRequested;
                    }

                    // Disconnect and dispose serial service
                    _serialConnectionService.Disconnect();
                    _serialConnectionService.Dispose();
                }

                // Clear static singleton instances to allow GC
                PluginSettings.ClearInstance();

                Logger.Info("[MLAstro] MLAstroController disposed");
            }

            _disposed = true;
        }
         
        ~MLAstroController()
        {
            Dispose(false);
        }

        #endregion

        private class RelayCommand : ICommand
        {
            private readonly Action _execute;

            public RelayCommand(Action execute)
            {
                _execute = execute;
            }

            public bool CanExecute(object? parameter) => true;

            public event EventHandler? CanExecuteChanged
            {
                add => CommandManager.RequerySuggested += value;
                remove => CommandManager.RequerySuggested -= value;
            }

            public void Execute(object? parameter) => _execute();
        }

    }

}