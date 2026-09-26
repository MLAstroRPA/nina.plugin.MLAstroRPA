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
    /// <summary>Connection type to MLAstroRPA.</summary>
    public enum MlastroTransportMode
    {
        /// <summary>Connection over a COM port (USB serial) - the default, unchanged from earlier versions.</summary>
        Serial = 0,

        /// <summary>Connection over WebSocket (mDNS "MLAstroRPA.local" or an IP).</summary>
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
        /// Connection type to MLAstroRPA: Serial (COM port) or Wireless (WebSocket over mDNS/IP).
        /// Pick one of the two only - a single transport holds control at a time.
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
        /// Device address in Wireless mode: an mDNS hostname (default "MLAstroRPA.local")
        /// or a direct IP (e.g. "192.168.4.1") when mDNS does not resolve.
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
        /// Fixed baud rate of the MLAstroRPA firmware (115200 8N1). No longer configurable:
        /// it always returns 115200 whatever an earlier session stored (the setter stays a no-op so old bindings keep working).
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
            // This value MIRRORS the telemetry (STAi as assigned by the router over DHCP), so it is NOT
            // given a fake default: until telemetry arrives it stays EMPTY (an empty UI means no IP yet).
            get => GetString(nameof(WifiIp), string.Empty);
            set => SetString(value);
        }

        // ===== External correction over the TPPA broker (SOFTWARE SETTING tab) =====

        /// <summary>
        /// Turns the whole broker integration with the Three Point Polar Alignment plugin on or off: it
        /// announces the capabilities, receives the measured errors, drives the motors and reports the end
        /// of the session.
        /// </summary>
        public bool TppaBrokerEnabled
        {
            get => GetBool(nameof(TppaBrokerEnabled), true);
            set => SetBool(value);
        }

        /// <summary>Correction strategy: Both = one ALIGN command for both axes, Auto = only the axis with the larger error.</summary>
        public BridgeAxisMode CorrectionAxisMode
        {
            get
            {
                var value = GetString(nameof(CorrectionAxisMode), BridgeAxisMode.Both.ToString());
                return Enum.TryParse<BridgeAxisMode>(value, true, out var mode) ? mode : BridgeAxisMode.Both;
            }
            set => SetString(value.ToString());
        }

        /// <summary>Safety factor multiplied into the measured error before the move is sent (1.0 = correct exactly the measured error).</summary>
        public double CorrectionSafetyFactor
        {
            get => GetDouble(nameof(CorrectionSafetyFactor), 0.75);
            set => SetDouble(value);
        }

        /// <summary>Upper limit for a single correction step (arcmin), so one large error cannot produce a dangerous slew.</summary>
        public double CorrectionMaxStepArcMin
        {
            get => GetDouble(nameof(CorrectionMaxStepArcMin), 60);
            set => SetDouble(value);
        }

        /// <summary>Overshoot: deliberately travel past the target so the next measurement corrects the remainder.</summary>
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
        /// Turns the overshoot on when the altitude axis has to move UP. Only effective while CorrectionOvershootEnabled is on.
        /// </summary>
        public bool CorrectionOvershootUpEnabled
        {
            get => GetBool(nameof(CorrectionOvershootUpEnabled), true);
            set => SetBool(value);
        }

        /// <summary>
        /// Turns the overshoot on when the altitude axis has to move DOWN. Only effective while CorrectionOvershootEnabled is on.
        /// </summary>
        public bool CorrectionOvershootDownEnabled
        {
            get => GetBool(nameof(CorrectionOvershootDownEnabled), true);
            set => SetBool(value);
        }

        /// <summary>
        /// Reverses the sign (software) of the azimuth axis: flips the direction of the moves this plugin
        /// sends during an external correction session. Nothing is written to FRAM or the firmware - this is
        /// not the "Reverse Direction" of the HARDWARE SETTING tab (that one reverses the firmware, AzRD:).
        /// </summary>
        public bool SoftwareReverseAzimuth
        {
            get => GetBool(nameof(SoftwareReverseAzimuth), false);
            set => SetBool(value);
        }

        /// <summary>
        /// Reverses the sign (software) of the altitude axis: flips the direction of the moves this plugin
        /// sends during an external correction session. Nothing is written to FRAM or the firmware - this is
        /// not the "Reverse Direction" of the HARDWARE SETTING tab (that one reverses the firmware, AlRD:).
        /// </summary>
        public bool SoftwareReverseAltitude
        {
            get => GetBool(nameof(SoftwareReverseAltitude), false);
            set => SetBool(value);
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
