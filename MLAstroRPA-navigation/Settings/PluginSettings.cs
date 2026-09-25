using NINA.Profile;
using NINA.Profile.Interfaces;
using System;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Globalization;
using System.Runtime.CompilerServices;
using MLAstroRPA.Broker;
using MLAstroRPA.Dockables;

namespace MLAstroRPA.Settings
{
    /// <summary>Kiểu kết nối tới MLAstroRPA.</summary>
    public enum MlastroTransportMode
    {
        /// <summary>Kết nối qua cổng COM (USB Serial) - mặc định, giữ nguyên hành vi cũ.</summary>
        Serial = 0,

        /// <summary>Kết nối qua WebSocket (mDNS "MLAstroRPA.local" hoặc IP).</summary>
        Wireless = 1
    }

    [Export]
    [PartCreationPolicy(CreationPolicy.Shared)]
    public class PluginSettings : INotifyPropertyChanged
    {
        private readonly PluginOptionsAccessor _optionsAccessor;

        // Static singleton instance
        private static PluginSettings? _instance;
        private static readonly object _instanceLock = new();

        /// <summary>
        /// Gets the singleton instance of PluginSettings.
        /// </summary>
        public static PluginSettings Instance
        {
            get => _instance!;
        }

        /// <summary>
        /// Clears the static singleton instance and events. Called during plugin cleanup.
        /// </summary>
        public static void ClearInstance()
        {
            lock (_instanceLock)
            {
                _instance = null;
                // Clear all static event subscribers to prevent memory leaks
                DataSourceModeChanged = null;
            }
        }

        public static event EventHandler<PolarAlignmentDataSourceMode>? DataSourceModeChanged;

        public event PropertyChangedEventHandler? PropertyChanged;

        [ImportingConstructor]
        public PluginSettings(IProfileService profileService)
        {
            var pluginGuid = PluginOptionsAccessor.GetAssemblyGuid(typeof(PluginSettings));
            _optionsAccessor = new PluginOptionsAccessor(profileService, pluginGuid ?? Guid.Empty);

            // Register this instance as the singleton
            lock (_instanceLock)
            {
                _instance ??= this;
            }
        }

        public PolarAlignmentDataSourceMode DataSourceMode
        {
            get
            {
                var value = _optionsAccessor.GetValueString(nameof(DataSourceMode), PolarAlignmentDataSourceMode.Auto.ToString());
                return Enum.TryParse<PolarAlignmentDataSourceMode>(value, true, out var mode) ? mode : PolarAlignmentDataSourceMode.Auto;
            }
            set
            {
                var currentValue = DataSourceMode;
                _optionsAccessor.SetValueString(nameof(DataSourceMode), value.ToString());
                OnPropertyChanged();

                if (currentValue != value)
                {
                    DataSourceModeChanged?.Invoke(this, value);
                }
            }
        }

        public string ComPort
        {
            get => GetString(nameof(ComPort), "COM1");
            set => SetString(value);
        }

        /// <summary>
        /// Kiểu kết nối tới MLAstroRPA: Serial (cổng COM) hoặc Wireless (WebSocket qua mDNS/IP).
        /// Chỉ chọn 1 trong 2 - mỗi lúc chỉ có 1 transport giữ quyền điều khiển.
        /// </summary>
        public MlastroTransportMode TransportMode
        {
            get
            {
                var value = _optionsAccessor.GetValueString(nameof(TransportMode), MlastroTransportMode.Serial.ToString());
                return Enum.TryParse<MlastroTransportMode>(value, true, out var mode) ? mode : MlastroTransportMode.Serial;
            }
            set
            {
                var current = TransportMode;
                _optionsAccessor.SetValueString(nameof(TransportMode), value.ToString());
                OnPropertyChanged();
                if (current != value)
                {
                    TransportModeChanged?.Invoke(this, value);
                }
            }
        }

        public static event EventHandler<MlastroTransportMode>? TransportModeChanged;

        /// <summary>
        /// Địa chỉ thiết bị ở chế độ Wireless: hostname mDNS (mặc định "MLAstroRPA.local")
        /// hoặc IP trực tiếp (vd "192.168.4.1") khi mDNS không hoạt động.
        /// </summary>
        public string MlaHost
        {
            get => GetString(nameof(MlaHost), "MLAstroRPA.local");
            set => SetString(value);
        }

        public int MlaPort
        {
            get => GetInt(nameof(MlaPort), 80);
            set => SetInt(value);
        }

        public string MlaPath
        {
            get => GetString(nameof(MlaPath), "/ws");
            set => SetString(value);
        }

        /// <summary>
        /// Baudrate cứng của firmware MLAstroRPA (115200 8N1). Không còn cho cấu hình:
        /// luôn trả 115200 bất kể giá trị đã lưu từ phiên trước (giữ setter no-op để tương thích binding cũ).
        /// </summary>
        public int BaudRate
        {
            get => 115200;
            set { }
        }

        public int HandshakeTimeoutMilliseconds
        {
            get => GetInt(nameof(HandshakeTimeoutMilliseconds), 300);
            set => SetInt(value);
        }

        public int PollingIntervalMilliseconds
        {
            get => GetInt(nameof(PollingIntervalMilliseconds), 300);
            set => SetInt(value);
        }

        public bool ShowHardlimitMonitor
        {
            get => GetBool(nameof(ShowHardlimitMonitor), false);
            set => SetBool(value);
        }

        public bool ShowSteps
        {
            get => GetBool(nameof(ShowSteps), false);
            set => SetBool(value);
        }

        public double CalibTravelAz
        {
            get => GetDouble(nameof(CalibTravelAz), 20);
            set => SetDouble(value);
        }

        public double CalibTravelAlt
        {
            get => GetDouble(nameof(CalibTravelAlt), 30);
            set => SetDouble(value);
        }

        public int AzSgThrs
        {
            get => GetInt(nameof(AzSgThrs), 110);
            set => SetInt(value);
        }

        public int AltSgThrs
        {
            get => GetInt(nameof(AltSgThrs), 110);
            set => SetInt(value);
        }

        public int StallTime
        {
            get => GetInt(nameof(StallTime), 255);
            set => SetInt(value);
        }

        public int EscapeRotations
        {
            get => GetInt(nameof(EscapeRotations), 3);
            set => SetInt(value);
        }

        public bool EnableHardLimit
        {
            get => GetBool(nameof(EnableHardLimit), false);
            set => SetBool(value);
        }

        public double LimitAzMin
        {
            get => GetDouble(nameof(LimitAzMin), -9);
            set => SetDouble(value);
        }

        public double LimitAzMax
        {
            get => GetDouble(nameof(LimitAzMax), 9);
            set => SetDouble(value);
        }

        public double LimitAltMin
        {
            get => GetDouble(nameof(LimitAltMin), -14);
            set => SetDouble(value);
        }

        public double LimitAltMax
        {
            get => GetDouble(nameof(LimitAltMax), 14);
            set => SetDouble(value);
        }

        public bool AzReverse
        {
            get => GetBool(nameof(AzReverse), false);
            set => SetBool(value);
        }

        public int AzCurrentRun
        {
            get => GetInt(nameof(AzCurrentRun), 1000);
            set => SetInt(value);
        }

        public int AzCurrentHold
        {
            get => GetInt(nameof(AzCurrentHold), 500);
            set => SetInt(value);
        }

        public int AzBooster
        {
            get => GetInt(nameof(AzBooster), 120);
            set => SetInt(value);
        }

        public int AzCoolStep
        {
            get => GetInt(nameof(AzCoolStep), 70);
            set => SetInt(value);
        }

        public int AzMicrosteps
        {
            get => GetInt(nameof(AzMicrosteps), 16);
            set => SetInt(value);
        }

        public int AzAccel
        {
            get => GetInt(nameof(AzAccel), 30000);
            set => SetInt(value);
        }

        public int AzDecel
        {
            get => GetInt(nameof(AzDecel), 30000);
            set => SetInt(value);
        }

        public double AzStepsPerDegree
        {
            get => GetDouble(nameof(AzStepsPerDegree), 1000);
            set => SetDouble(value);
        }

        public int AzMode
        {
            get => GetInt(nameof(AzMode), 0);
            set => SetInt(value);
        }

        public bool AltReverse
        {
            get => GetBool(nameof(AltReverse), false);
            set => SetBool(value);
        }

        public int AltCurrentRun
        {
            get => GetInt(nameof(AltCurrentRun), 1000);
            set => SetInt(value);
        }

        public int AltCurrentHold
        {
            get => GetInt(nameof(AltCurrentHold), 500);
            set => SetInt(value);
        }

        public int AltBooster
        {
            get => GetInt(nameof(AltBooster), 120);
            set => SetInt(value);
        }

        public int AltCoolStep
        {
            get => GetInt(nameof(AltCoolStep), 70);
            set => SetInt(value);
        }

        public int AltMicrosteps
        {
            get => GetInt(nameof(AltMicrosteps), 16);
            set => SetInt(value);
        }

        public int AltAccel
        {
            get => GetInt(nameof(AltAccel), 30000);
            set => SetInt(value);
        }

        public int AltDecel
        {
            get => GetInt(nameof(AltDecel), 30000);
            set => SetInt(value);
        }

        public double AltStepsPerDegree
        {
            get => GetDouble(nameof(AltStepsPerDegree), 1000);
            set => SetDouble(value);
        }

        public int AltMode
        {
            get => GetInt(nameof(AltMode), 0);
            set => SetInt(value);
        }

        public bool BacklashEnabled
        {
            get => GetBool(nameof(BacklashEnabled), false);
            set => SetBool(value);
        }

        public int BacklashAz
        {
            get => GetInt(nameof(BacklashAz), 100);
            set => SetInt(value);
        }

        public int BacklashAlt
        {
            get => GetInt(nameof(BacklashAlt), 80);
            set => SetInt(value);
        }

        public bool OvershootEnabled
        {
            get => GetBool(nameof(OvershootEnabled), false);
            set => SetBool(value);
        }

        public bool OvershootMoveUp
        {
            get => GetBool(nameof(OvershootMoveUp), false);
            set => SetBool(value);
        }

        public bool OvershootMoveDown
        {
            get => GetBool(nameof(OvershootMoveDown), false);
            set => SetBool(value);
        }

        public int OvershootDegrees
        {
            get => GetInt(nameof(OvershootDegrees), 0);
            set => SetInt(value);
        }

        public int OvershootMinutes
        {
            get => GetInt(nameof(OvershootMinutes), 0);
            set => SetInt(value);
        }

        public int OvershootSeconds
        {
            get => GetInt(nameof(OvershootSeconds), 0);
            set => SetInt(value);
        }

        public string ApSsid
        {
            get => GetString(nameof(ApSsid), string.Empty);
            set => SetString(value);
        }

        public string ApPass
        {
            get => GetString(nameof(ApPass), string.Empty);
            set => SetString(value);
        }

        public string ApIp
        {
            get => GetString(nameof(ApIp), "192.168.4.1");
            set => SetString(value);
        }

        public string ApSubnet
        {
            get => GetString(nameof(ApSubnet), "255.255.255.0");
            set => SetString(value);
        }

        public string WifiSsid
        {
            get => GetString(nameof(WifiSsid), string.Empty);
            set => SetString(value);
        }

        public string WifiPass
        {
            get => GetString(nameof(WifiPass), string.Empty);
            set => SetString(value);
        }

        public string WifiIp
        {
            // Giá trị này là ẢNH PHẢN CHIẾU của telemetry (STAi do router cấp qua DHCP) nên KHÔNG đặt
            // giá trị mặc định giả: khi chưa có telemetry thì để RỖNG (UI hiện trống = chưa có IP).
            get => GetString(nameof(WifiIp), string.Empty);
            set => SetString(value);
        }

        // ===== External correction qua broker TPPA (tab SOFTWARE SETTING) =====

        /// <summary>
        /// Bật/tắt toàn bộ tích hợp broker với plugin Three Point Polar Alignment: tự announce
        /// capabilities, nhận sai số, điều khiển motor và báo kết thúc phiên.
        /// </summary>
        public bool TppaBrokerEnabled
        {
            get => GetBool(nameof(TppaBrokerEnabled), true);
            set => SetBool(value);
        }

        /// <summary>Chiến lược sửa: Both = 1 lệnh ALIGN 2 trục, Auto = chỉ trục có sai số lớn hơn.</summary>
        public ExternalAxisMode CorrectionAxisMode
        {
            get
            {
                var value = GetString(nameof(CorrectionAxisMode), ExternalAxisMode.Both.ToString());
                return Enum.TryParse<ExternalAxisMode>(value, true, out var mode) ? mode : ExternalAxisMode.Both;
            }
            set => SetString(value.ToString());
        }

        /// <summary>Hệ số an toàn nhân vào sai số đo được trước khi gửi lệnh (1.0 = sửa đúng bằng sai số).</summary>
        public double CorrectionSafetyFactor
        {
            get => GetDouble(nameof(CorrectionSafetyFactor), 0.75);
            set => SetDouble(value);
        }

        /// <summary>Giới hạn biên độ mỗi lần sửa (arcmin) để một sai số lớn không gây cú quay nguy hiểm.</summary>
        public double CorrectionMaxStepArcMin
        {
            get => GetDouble(nameof(CorrectionMaxStepArcMin), 60);
            set => SetDouble(value);
        }

        /// <summary>Bật overshoot: cố tình vượt target một đoạn rồi quay lại để triệt backlash.</summary>
        public bool CorrectionOvershootEnabled
        {
            get => GetBool(nameof(CorrectionOvershootEnabled), false);
            set => SetBool(value);
        }

        public double CorrectionOvershootUpArcMin
        {
            get => GetDouble(nameof(CorrectionOvershootUpArcMin), 5);
            set => SetDouble(value);
        }

        public double CorrectionOvershootDownArcMin
        {
            get => GetDouble(nameof(CorrectionOvershootDownArcMin), 5);
            set => SetDouble(value);
        }

        /// <summary>
        /// Bật overshoot khi trục Altitude phải đi LÊN. Chỉ có hiệu lực khi CorrectionOvershootEnabled bật.
        /// </summary>
        public bool CorrectionOvershootUpEnabled
        {
            get => GetBool(nameof(CorrectionOvershootUpEnabled), true);
            set => SetBool(value);
        }

        /// <summary>
        /// Bật overshoot khi trục Altitude phải đi XUỐNG. Chỉ có hiệu lực khi CorrectionOvershootEnabled bật.
        /// </summary>
        public bool CorrectionOvershootDownEnabled
        {
            get => GetBool(nameof(CorrectionOvershootDownEnabled), true);
            set => SetBool(value);
        }

        /// <summary>
        /// Đảo dấu (software) cho trục Azimuth: lật hướng các lệnh dịch chuyển do plugin gửi trong
        /// phiên external correction. Không ghi gì xuống FRAM/firmware - khác "Reverse Direction"
        /// trong tab CONFIGURATION (đảo chiều ở firmware, ghi AzRD:).
        /// </summary>
        public bool SoftwareReverseAzimuth
        {
            get => GetBool(nameof(SoftwareReverseAzimuth), false);
            set => SetBool(value);
        }

        /// <summary>
        /// Đảo dấu (software) cho trục Altitude: lật hướng các lệnh dịch chuyển do plugin gửi trong
        /// phiên external correction. Không ghi gì xuống FRAM/firmware - khác "Reverse Direction"
        /// trong tab CONFIGURATION (đảo chiều ở firmware, ghi AlRD:).
        /// </summary>
        public bool SoftwareReverseAltitude
        {
            get => GetBool(nameof(SoftwareReverseAltitude), false);
            set => SetBool(value);
        }

        /// <summary>Bù backlash trục Azimuth (arcmin): vượt target rồi quay lại một đoạn nhỏ.</summary>
        public double CorrectionAzBacklashArcMin
        {
            get => GetDouble(nameof(CorrectionAzBacklashArcMin), 0);
            set => SetDouble(value);
        }

        /// <summary>Trần thời gian của cả phiên sửa tự động (giây).</summary>
        public int CorrectionTimeoutSec
        {
            get => GetInt(nameof(CorrectionTimeoutSec), 1800);
            set => SetInt(value);
        }

        /// <summary>
        /// Số lần đo liên tiếp đạt tolerance trước khi yêu cầu TPPA chốt phiên. TPPA vẫn verify lại
        /// bằng policy của nó, nên giá trị này chỉ là điều kiện kích hoạt phía controller.
        /// </summary>
        public int CorrectionConsecutiveToFinish
        {
            get => GetInt(nameof(CorrectionConsecutiveToFinish), 2);
            set => SetInt(value);
        }

        private string GetString(string propertyName, string defaultValue)
        {
            return _optionsAccessor.GetValueString(propertyName, defaultValue);
        }

        private void SetString(string value, [CallerMemberName] string propertyName = null!)
        {
            _optionsAccessor.SetValueString(propertyName, value ?? string.Empty);
            OnPropertyChanged(propertyName);
        }

        private int GetInt(string propertyName, int defaultValue)
        {
            return _optionsAccessor.GetValueInt32(propertyName, defaultValue);
        }

        private void SetInt(int value, [CallerMemberName] string propertyName = null!)
        {
            _optionsAccessor.SetValueInt32(propertyName, value);
            OnPropertyChanged(propertyName);
        }

        private bool GetBool(string propertyName, bool defaultValue)
        {
            var value = _optionsAccessor.GetValueString(propertyName, defaultValue.ToString());
            return bool.TryParse(value, out var parsedValue) ? parsedValue : defaultValue;
        }

        private void SetBool(bool value, [CallerMemberName] string propertyName = null!)
        {
            _optionsAccessor.SetValueString(propertyName, value.ToString());
            OnPropertyChanged(propertyName);
        }

        private double GetDouble(string propertyName, double defaultValue)
        {
            var value = _optionsAccessor.GetValueString(propertyName, defaultValue.ToString(CultureInfo.InvariantCulture));
            return double.TryParse(value, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsedValue)
                ? parsedValue
                : defaultValue;
        }

        private void SetDouble(double value, [CallerMemberName] string propertyName = null!)
        {
            _optionsAccessor.SetValueString(propertyName, value.ToString(CultureInfo.InvariantCulture));
            OnPropertyChanged(propertyName);
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null!)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
