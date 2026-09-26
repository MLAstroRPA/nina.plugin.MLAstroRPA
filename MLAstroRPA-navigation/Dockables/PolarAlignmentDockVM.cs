using NINA.Equipment.Interfaces.ViewModel;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.ViewModel;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MLAstroRPA.Settings;
using MLAstroRPA.Services;

namespace MLAstroRPA.Dockables
{ 
    [Export]
    [PartCreationPolicy(CreationPolicy.Shared)]
    public class PolarAlignmentDockVM : DockableVM, IDisposable
    { 
        private readonly PluginSettings _settings;
        private readonly SerialConnectionService _serialService;
        private System.Timers.Timer? _jogWatchdogTimer;
        private string? _currentJogCommand = null;
        private readonly object _jogLock = new();

        // Per-direction lock: set when the firmware REFUSES a jog command because of a soft limit
        // (ERROR telemetry token `CmdRf` -> codes RfJogAz / RfJogAl). Unlocked by pressing the OPPOSITE direction
        // or automatically after JOG_UNBLOCK_DELAY_MS.
        private bool _jogAltUpBlocked;
        private bool _jogAltDownBlocked;
        private bool _jogAzLeftBlocked;
        private bool _jogAzRightBlocked;
        private System.Windows.Threading.DispatcherTimer? _jogUnblockTimer;

        /// <summary>How long after the last refusal the arrow buttons unlock themselves.</summary>
        private const int JOG_UNBLOCK_DELAY_MS = 2000;

        private bool _disposed = false;

        // Static instance for cleanup during plugin teardown
        private static PolarAlignmentDockVM? _instance;
        private static readonly object _instanceLock = new();

        /// <summary>
        /// Gets the current instance of PolarAlignmentDockVM for cleanup purposes.
        /// </summary>
        public static PolarAlignmentDockVM Instance => _instance!;

        // Header Properties
        private string _firmwareVersion = "unknown";
        private string _spiffsVersion = "1.0.118";
        private string _systemStatus = "Idle";
        private Brush _statusForeground = Brushes.White;
        private Brush _connectionStatusColor = Brushes.Gray;
        private string _connectionStatusText = "Disconnected";
        private Visibility _controlsVisibility = Visibility.Collapsed;

        // HeaderBar - the AP/STA line uses COLOURED EMOJI (NINA/.NET 8 renders colour emoji through Segoe UI Emoji):
        //   AP:  <green dot> = PC goes through the hotspot (Connected) - <wireless mark> = AP is up but the PC goes another way (Ready) - <cross> = AP error
        //   STA: the icon uses a monochrome font (XAML: Segoe UI Symbol), so it can be COLOURED:
        //        glyph = <bars> internet - <bars+bang> router but no internet - <cross> not joined to the router;
        //        colour = green when THIS PC goes through STA - blue when the PC goes another way
        private string _apIconGlyph = "\u274C";
        private string _apStatusText = string.Empty;   // IP of the AP (or "-" while unknown)
        private bool _apReady;
        private string _apIp = string.Empty;
        private string _staIconText = "\u274C";
        // Per-line icon colour (it only applies to a monochrome text glyph - coloured emoji ignores Foreground).
        private Brush _apIconBrush = Brushes.Gray;
        private Brush _staIconBrush = Brushes.Gray;

        private string _staStatusText = "none";

        // Manual Movement Properties
        private int _currentSpeed = 3;
        private bool _isRelativeMode = false;
        private Visibility _relativeOptionsVisibility = Visibility.Collapsed;
        private int _relativeDegrees = 0;
        private int _relativeMinutes = 0;
        private int _relativeSeconds = 1;

        // Position Properties
        private string _azPosition = "+0° 00' 00\"";
        private string _altPosition = "+0° 00' 00\"";
        private string _azSteps = "0";
        private string _altSteps = "0";
        private string _azOutSpeed = "0.000";
        private string _altOutSpeed = "0.000";
        private string _azMotorSpeed = "0.000";
        private string _altMotorSpeed = "0.000";
        private string _homedStatus = "No";

        // Alignment Properties
        private int _azErrorDeg = 0;
        private int _azErrorMin = 0;
        private int _azErrorSec = 0;
        private bool _azErrorRight = false;
        private int _altErrorDeg = 0;
        private int _altErrorMin = 0;
        private int _altErrorSec = 0;
        private bool _altErrorUp = false;

        // Flag to track if we're syncing from telemetry (prevents sending command back to hardware)
        private bool _isSyncingFromTelemetry = false;

        // Flag to enable/disable alignment input editing (when ON: user can edit, telemetry sync paused)
        private bool _isAlignmentModifyMode = false;

        // Flag for automated adjustment mode (disables manual controls and telemetry sync)
        private bool _isAutomatedAdjustment = false;

        // Flag to pause telemetry sync for relative values when user is editing
        private bool _isEditingRelativeValues = false;

        // Flag to pause telemetry sync for alignment error fields when the user is editing
        private bool _isEditingAlignment = false;

        /// <summary>
        /// True while the caret sits in a dock D/M/S box (Tag "rel"/"az"/"alt"). It is the second safety
        /// catch for the "never overwrite while editing" rule: the Start/EndEditing flags can
        /// drift (the caret enters a box with the mouse and leaves without raising LostKeyboardFocus).
        /// </summary>
        private static bool IsDmsInputFocused()
        {
            // Telemetry is pushed from the COM port background thread. WPF requires InputManager/Keyboard
            // access on the UI thread; calling it from a background thread can throw => the rest of the telemetry is
            // dropped (motion/jog state updates are lost => the dock locks manual control). That is why this only
            // checks while running on the UI thread.
            var app = Application.Current;
            if (app == null || !app.Dispatcher.CheckAccess())
            {
                return false;
            }

            var tag = (Keyboard.FocusedElement as FrameworkElement)?.Tag as string;
            return tag == "deg" || tag == "min" || tag == "sec" || tag == "az" || tag == "alt";
        }

        // Alarm History (industrial HMI style): one row per driver error/warning code,
        // showing the activation time and (once cleared) the end time on the SAME row.
        private const int AlarmHistoryMaxEntries = 100;
        private readonly ObservableCollection<DriverAlarm> _alarmHistory = new();
        private bool _hasActiveErrors;
        private bool _hasActiveWarnings;
        private Visibility _alarmHistoryVisibility = Visibility.Collapsed;

        public override string ContentId => "MLAstroRPA";

        #region Header Properties

        public string FirmwareVersion
        {
            get => _firmwareVersion;
            set => SetProperty(ref _firmwareVersion, value);
        }

        public string SpiffsVersion
        {
            get => _spiffsVersion;
            set => SetProperty(ref _spiffsVersion, value);
        }

        public string SystemStatus
        {
            get => _systemStatus;
            set
            {
                if (SetProperty(ref _systemStatus, value))
                {
                    UpdateStatusColor();
                    OnPropertyChanged(nameof(CanManualControl));
                    NotifyCanJogChanged();
                    OnPropertyChanged(nameof(CanAutomaticControl));
                    OnPropertyChanged(nameof(CanAlign));
                    OnPropertyChanged(nameof(ResetErrorButtonVisibility));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        /// <summary>
        /// Visibility of the RESET ERROR button: only visible when the system status is ERROR.
        /// </summary>
        public Visibility ResetErrorButtonVisibility =>
            SystemStatus.Equals("ERROR", StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible
                : Visibility.Collapsed;

        public Brush StatusForeground
        {
            get => _statusForeground;
            private set => SetProperty(ref _statusForeground, value);
        }

        /// <summary>
        /// Industrial-HMI-style alarm history. Each row is a driver error/warning code that
        /// became active at <see cref="DriverAlarm.ActivatedAt"/> and, once cleared, shows
        /// the end time on the SAME row via <see cref="DriverAlarm.ClearedAt"/>.
        /// Display order: **newest on top**, older entries below (see OnErrorStateChanged).
        /// </summary>
        public ObservableCollection<DriverAlarm> AlarmHistory => _alarmHistory;

        /// <summary>
        /// True while at least one driver code is in ERROR state (value 2).
        /// Disables manual/automatic movement while the system is error-locked.
        /// </summary>
        public bool HasActiveErrors
        {
            get => _hasActiveErrors;
            private set
            {
                if (SetProperty(ref _hasActiveErrors, value))
                {
                    OnPropertyChanged(nameof(CanManualControl));
                    NotifyCanJogChanged();
                    OnPropertyChanged(nameof(CanAutomaticControl));
                    OnPropertyChanged(nameof(CanAlign));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        /// <summary>
        /// True while at least one driver code is in WARNING state (value 1).
        /// </summary>
        public bool HasActiveWarnings
        {
            get => _hasActiveWarnings;
            private set => SetProperty(ref _hasActiveWarnings, value);
        }

        public Visibility AlarmHistoryVisibility
        {
            get => _alarmHistoryVisibility;
            private set => SetProperty(ref _alarmHistoryVisibility, value);
        }

        public Brush ConnectionStatusColor
        {
            get => _connectionStatusColor;
            private set => SetProperty(ref _connectionStatusColor, value);
        }

        public string ConnectionStatusText
        {
            get => _connectionStatusText;
            set => SetProperty(ref _connectionStatusText, value);
        }

        public Visibility ControlsVisibility
        {
            get => _controlsVisibility;
            private set => SetProperty(ref _controlsVisibility, value);
        }

        /// <summary>
        /// The "AP: ..." line in the HeaderBar (it replaced the old "Connection:" label) - lowercase, same font as the STA line:
        ///   "AP: connected <IP>" = THIS PC runs through the ESP32 hotspot (the firmware reports link=AP);
        ///   "AP: ready <IP>"     = the AP is up and has an IP but the PC goes another way (STA/USB cable);
        ///   "AP: error"          = the AP did not come up / has no IP;
        ///   "AP: -"              = the device is not reachable yet, so the AP state is unknown.
        /// </summary>
        public string ApStatusText
        {
            get => _apStatusText;
            private set => SetProperty(ref _apStatusText, value);
        }

        /// <summary>
        /// The "STA: <icon> IP" line: the text is the LAN IP the router gave the ESP32.
        /// The network icon (3 Paths in the HeaderBar) follows StaIcon*Visibility:
        /// a slash = not joined to the router, a bang = router but no internet, plain = internet.
        /// </summary>
        public string StaStatusText
        {
            get => _staStatusText;
            private set => SetProperty(ref _staStatusText, value);
        }

        /// <summary>AP line icon: <wireless mark> (PC goes through the hotspot) - <green dot> (AP up, another route) - <cross> (AP error).</summary>
        public string ApIconGlyph
        {
            get => _apIconGlyph;
            private set => SetProperty(ref _apIconGlyph, value);
        }

        /// <summary>STA line icon: <bars> (internet) - <bars+bang> (router, no internet) - <cross> (not joined).</summary>
        public string StaIconText
        {
            get => _staIconText;
            private set => SetProperty(ref _staIconText, value);
        }

        /// <summary>AP line icon colour: grey (unknown) - green (Connected) - blue (Ready) - red (Error).</summary>
        public Brush ApIconBrush
        {
            get => _apIconBrush;
            private set => SetProperty(ref _apIconBrush, value);
        }

        /// <summary>STA line icon colour: green (internet) - amber (router, no internet) - red (not joined to the router).</summary>
        public Brush StaIconBrush
        {
            get => _staIconBrush;
            private set => SetProperty(ref _staIconBrush, value);
        }

        #endregion

        #region Manual Movement Properties

        public int CurrentSpeed
        {
            get => _currentSpeed;
            set => SetProperty(ref _currentSpeed, value);
        }

        public bool IsRelativeMode
        {
            get => _isRelativeMode;
            set
            {
                if (SetProperty(ref _isRelativeMode, value))
                {
                    RelativeOptionsVisibility = value ? Visibility.Visible : Visibility.Collapsed;

                    // Send mode switch command
                    SendCommand($"JoRe:{(value ? 1 : 0)}\n");
                }
            }
        }

        public Visibility RelativeOptionsVisibility
        {
            get => _relativeOptionsVisibility;
            private set => SetProperty(ref _relativeOptionsVisibility, value);
        }

        public int RelativeDegrees
        {
            get => _relativeDegrees;
            // Limits by design: degrees 0-5, minutes 0-59, seconds 0-59 (typed values and +/- buttons are both clamped).
            set
            {
                SetProperty(ref _relativeDegrees, Math.Max(0, Math.Min(5, value)));
                // Raise even when the clamp lands on the old value: when the user types a number past the limit
                // (e.g. "6" while it holds 5) the TextBox would keep showing the wrong number without a raise.
                OnPropertyChanged();
            }
        }

        public int RelativeMinutes
        {
            get => _relativeMinutes;
            set
            {
                SetProperty(ref _relativeMinutes, Math.Max(0, Math.Min(59, value)));
                OnPropertyChanged();
            }
        }

        public int RelativeSeconds
        {
            get => _relativeSeconds;
            set
            {
                SetProperty(ref _relativeSeconds, Math.Max(0, Math.Min(59, value)));
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// Start editing relative values - pause telemetry sync
        /// </summary>
        public void StartEditingRelative()
        {
            _isEditingRelativeValues = true;
        }

        /// <summary>
        /// End editing relative values - resume the telemetry sync. Only called when focus leaves the D/M/S boxes
        /// (see OnDockLostKeyboardFocus in the code-behind).
        /// </summary>
        public void EndEditingRelative()
        {
            _isEditingRelativeValues = false;
        }

        /// <summary>
        /// Send relative degrees to the hardware immediately. Do NOT release the telemetry pause here: the user may
        /// still be typing in the box, and an early release lets telemetry overwrite every keystroke. The pause is
        /// only released when focus leaves the D/M/S boxes.
        /// </summary>
        public void SendRelativeDegrees()
        {
            // Release the pause after sending: telemetry (the echo from the device) has to run again so the UI matches the device.
            // While the user is still typing, IsDmsInputFocused() keeps telemetry from overwriting.
            _isEditingRelativeValues = false;
            SendCommand($"ReDe:{_relativeDegrees}\n");
            Logger.Info($"[MLAstro] Sent ReDe:{_relativeDegrees}");
        }

        /// <summary>Send relative minutes to the hardware immediately (see the note on SendRelativeDegrees).</summary>
        public void SendRelativeMinutes()
        {
            _isEditingRelativeValues = false;
            SendCommand($"ReAM:{_relativeMinutes}\n");
            Logger.Info($"[MLAstro] Sent ReAM:{_relativeMinutes}");
        }

        /// <summary>Send relative seconds to the hardware immediately (see the note on SendRelativeDegrees).</summary>
        public void SendRelativeSeconds()
        {
            _isEditingRelativeValues = false;
            SendCommand($"ReAS:{_relativeSeconds}\n");
            Logger.Info($"[MLAstro] Sent ReAS:{_relativeSeconds}");
        }

        #endregion

        #region Position Properties

        public string AzPosition
        {
            get => _azPosition;
            set => SetProperty(ref _azPosition, value);
        }

        public string AltPosition
        {
            get => _altPosition;
            set => SetProperty(ref _altPosition, value);
        }

        // Moved position (relative to alignment start)
        private string _azMovedPosition = "+0° 00' 00\"";
        private string _altMovedPosition = "+0° 00' 00\"";

        public string AzMovedPosition
        {
            get => _azMovedPosition;
            set => SetProperty(ref _azMovedPosition, value);
        }

        public string AltMovedPosition
        {
            get => _altMovedPosition;
            set => SetProperty(ref _altMovedPosition, value);
        }

        public string AzSteps
        {
            get => _azSteps;
            set => SetProperty(ref _azSteps, value);
        }

        public string AltSteps
        {
            get => _altSteps;
            set => SetProperty(ref _altSteps, value);
        }

        public string AzOutSpeed
        {
            get => _azOutSpeed;
            set => SetProperty(ref _azOutSpeed, value);
        }

        public string AltOutSpeed
        {
            get => _altOutSpeed;
            set => SetProperty(ref _altOutSpeed, value);
        }

        public string AzMotorSpeed
        {
            get => _azMotorSpeed;
            set => SetProperty(ref _azMotorSpeed, value);
        }

        public string AltMotorSpeed
        {
            get => _altMotorSpeed;
            set => SetProperty(ref _altMotorSpeed, value);
        }

        public string HomedStatus
        {
            get => _homedStatus;
            set => SetProperty(ref _homedStatus, value);
        }

        #endregion

        #region Alignment Properties

        public int AzErrorDeg
        {
            get => _azErrorDeg;
            set => SetProperty(ref _azErrorDeg, value);
        }

        public int AzErrorMin
        {
            get => _azErrorMin;
            set => SetProperty(ref _azErrorMin, value);
        }

        public int AzErrorSec
        {
            get => _azErrorSec;
            set => SetProperty(ref _azErrorSec, value);
        }

        public bool AzErrorRight
        {
            get => _azErrorRight;
            set
            {
                if (SetProperty(ref _azErrorRight, value) && !_isSyncingFromTelemetry)
                {
                    // User changed - Send direction command to hardware immediately (1 = Right, 0 = Left)
                    SendCommand($"AzDi:{(value ? 1 : 0)}\n");
                    Logger.Info($"[MLAstro] User changed AzDi direction: {(value ? "Right" : "Left")}");
                }
            }
        }

        public int AltErrorDeg
        {
            get => _altErrorDeg;
            set => SetProperty(ref _altErrorDeg, value);
        }

        public int AltErrorMin
        {
            get => _altErrorMin;
            set => SetProperty(ref _altErrorMin, value);
        }

        public int AltErrorSec
        {
            get => _altErrorSec;
            set => SetProperty(ref _altErrorSec, value);
        }

        public bool AltErrorUp
        {
            get => _altErrorUp;
            set
            {
                if (SetProperty(ref _altErrorUp, value) && !_isSyncingFromTelemetry)
                {
                    // User changed - Send direction command to hardware immediately (1 = Up, 0 = Down)
                    SendCommand($"AlDi:{(value ? 1 : 0)}\n");
                    Logger.Info($"[MLAstro] User changed AlDi direction: {(value ? "Up" : "Down")}");
                }
            }
        }

        /// <summary>
        /// When ON (Modify): User can edit alignment values, telemetry sync is paused for these fields.
        /// When OFF (Done): Inputs are disabled, send all settings to hardware, telemetry updates continuously.
        /// </summary>
        public bool IsAlignmentModifyMode
        {
            get => _isAlignmentModifyMode;
            set
            {
                // Cannot modify when in automated adjustment mode
                if (_isAutomatedAdjustment && value)
                {
                    return;
                }

                var wasModifying = _isAlignmentModifyMode;
                if (SetProperty(ref _isAlignmentModifyMode, value))
                {
                    // When switching from Modify (ON) to Done (OFF), send all alignment settings
                    if (wasModifying && !value)
                    {
                        SendAlignmentSettings();
                    }
                    Logger.Info($"[MLAstro] Alignment modify mode: {(value ? "Modify" : "Done")}");
                }
            }
        }

        /// <summary>
        /// When ON: Disables Modify button and all Align buttons, stops telemetry sync for Polar Alignment.
        /// Used when external automation (e.g., plate solving) is controlling the alignment.
        /// </summary>
        public bool IsAutomatedAdjustment
        {
            get => _isAutomatedAdjustment;
            set
            {
                if (SetProperty(ref _isAutomatedAdjustment, value))
                {
                    // If turning on automated mode, force modify mode off
                    if (value && _isAlignmentModifyMode)
                    {
                        _isAlignmentModifyMode = false;
                        OnPropertyChanged(nameof(IsAlignmentModifyMode));
                    }
                    // Notify CanModify, CanAlign and CanManualControl changed for button enable/disable
                    OnPropertyChanged(nameof(CanModify));
                    OnPropertyChanged(nameof(CanAlign));
                    OnPropertyChanged(nameof(CanManualControl));
                    NotifyCanJogChanged();
                    OnPropertyChanged(nameof(CanAutomaticControl));
                    Logger.Info($"[MLAstro] Automated adjustment mode: {(value ? "ON" : "OFF")}");
                }
            }
        }

        /// <summary>
        /// Returns true if Modify button should be enabled (not in automated mode)
        /// </summary>
        public bool CanModify => !_isAutomatedAdjustment && !IsExternalLocked;

        /// <summary>
        /// Returns true while firmware telemetry reports manual movement or an idle state.
        /// Automated workflows own both axes and therefore disable every movement start control.
        /// </summary>
        public bool CanManualControl => !_isAutomatedAdjustment && !IsAutomaticMotion && !HasActiveErrors && !IsExternalLocked;

        // --- Arrow buttons (jog): IsEnabled = CanManualControl && not locked by a firmware refusal ---
        public bool CanJogAltUp => CanManualControl && !_jogAltUpBlocked;
        public bool CanJogAltDown => CanManualControl && !_jogAltDownBlocked;
        public bool CanJogAzLeft => CanManualControl && !_jogAzLeftBlocked;
        public bool CanJogAzRight => CanManualControl && !_jogAzRightBlocked;

        /// <summary>Tells the UI that the enabled state of the four arrow buttons may have changed.</summary>
        private void NotifyCanJogChanged()
        {
            OnPropertyChanged(nameof(CanJogAltUp));
            OnPropertyChanged(nameof(CanJogAltDown));
            OnPropertyChanged(nameof(CanJogAzLeft));
            OnPropertyChanged(nameof(CanJogAzRight));
        }

        private void SetJogBlocked(ref bool field, bool blocked, string propertyName)
        {
            if (field == blocked) return;
            field = blocked;
            OnPropertyChanged(propertyName);
        }

        /// <summary>Unlocks both directions of one axis (called when the user presses an arrow button again).</summary>
        private void UnblockJogAxis(string axis)
        {
            if (axis == "az")
            {
                SetJogBlocked(ref _jogAzLeftBlocked, false, nameof(CanJogAzLeft));
                SetJogBlocked(ref _jogAzRightBlocked, false, nameof(CanJogAzRight));
            }
            else
            {
                SetJogBlocked(ref _jogAltUpBlocked, false, nameof(CanJogAltUp));
                SetJogBlocked(ref _jogAltDownBlocked, false, nameof(CanJogAltDown));
            }
        }

        /// <summary>Splits a jog command ("MAzL:1") into axis + direction: az -1/+1 = left/right, alt +1/-1 = up/down.</summary>
        private static (string axis, int direction) ParseJogCommand(string command)
        {
            if (command.StartsWith("MAzL", StringComparison.OrdinalIgnoreCase)) return ("az", -1);
            if (command.StartsWith("MAzR", StringComparison.OrdinalIgnoreCase)) return ("az", 1);
            if (command.StartsWith("MAlU", StringComparison.OrdinalIgnoreCase)) return ("alt", 1);
            if (command.StartsWith("MAlD", StringComparison.OrdinalIgnoreCase)) return ("alt", -1);
            return (string.Empty, 0);
        }

        /// <summary>
        /// Schedules the arrow-button unlock JOG_UNBLOCK_DELAY_MS after the LATEST refusal (every
        /// refusal moves the deadline). The "axis is at its limit" warning is TEMPORARY - the axis may have been
        /// moved back inside its limits by another source (relative / auto / web), so the button is not locked forever.
        /// A DispatcherTimer keeps Tick on the UI thread (the four IsEnabled properties need no marshalling).
        /// </summary>
        private void StartJogUnblockTimer()
        {
            if (Application.Current?.Dispatcher == null) return;

            if (_jogUnblockTimer == null)
            {
                _jogUnblockTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(JOG_UNBLOCK_DELAY_MS)
                };
                _jogUnblockTimer.Tick += (s, e) =>
                {
                    _jogUnblockTimer?.Stop();
                    UnblockJogAxis("az");
                    UnblockJogAxis("alt");
                    Logger.Info("[MLAstro] Jog buttons auto-unblocked after soft-limit warning");
                };
            }

            _jogUnblockTimer.Stop();   // the deadline counts from the latest refusal
            _jogUnblockTimer.Start();
        }

        /// <summary>
        /// The firmware REFUSED a jog command because of a soft limit (ERROR telemetry `CmdRf` -> RfJogAz / RfJogAl).
        /// Handling: LOCK exactly the direction that was pressed, release the button once (send "MAzL:0") and send
        /// nothing else - a disabled button raises no MouseUp/MouseLeave in WPF, so the release handler
        /// never runs; StopJogMovement() has to be called explicitly (it also stops the 250 ms watchdog).
        /// </summary>
        private void HandleJogRefused(string axis)
        {
            string? cmd;
            lock (_jogLock) { cmd = _currentJogCommand; }
            if (string.IsNullOrEmpty(cmd)) return;      // the command was not issued by this plugin

            var (jogAxis, direction) = ParseJogCommand(cmd!);
            if (direction == 0 || !string.Equals(jogAxis, axis, StringComparison.Ordinal)) return;

            if (jogAxis == "az")
            {
                if (direction < 0) SetJogBlocked(ref _jogAzLeftBlocked, true, nameof(CanJogAzLeft));
                else SetJogBlocked(ref _jogAzRightBlocked, true, nameof(CanJogAzRight));
            }
            else
            {
                if (direction > 0) SetJogBlocked(ref _jogAltUpBlocked, true, nameof(CanJogAltUp));
                else SetJogBlocked(ref _jogAltDownBlocked, true, nameof(CanJogAltDown));
            }

            StartJogUnblockTimer();   // unlock itself after 2 s (besides pressing the opposite direction)
            StopJogMovement();   // sends ":0" + stops the watchdog + clears the running command -> nothing is sent again
            Logger.Info($"[MLAstro] Jog {axis} refused (soft limit) -> direction locked, jog released");
        }

        /// <summary>
        /// Returns true only when the firmware reports both motors are idle.
        /// Manual MOVING telemetry permits manual control but prevents starting an automatic workflow.
        /// </summary>
        public bool CanAutomaticControl => !_isAutomatedAdjustment && !IsMotionActive && !HasActiveErrors && !IsExternalLocked;

        public bool CanAlign => CanAutomaticControl;

        /// <summary>TPPA (external plugin) HOLDS control -> most controls and settings are locked
        /// (only the STOP/E-STOP buttons and the CONNECTION tab stay usable).</summary>
        public bool IsExternalLocked
        {
            get => _externalLocked;
            private set
            {
                if (_externalLocked == value) return;
                _externalLocked = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanModify));
                OnPropertyChanged(nameof(CanManualControl));
                NotifyCanJogChanged();
                OnPropertyChanged(nameof(CanAutomaticControl));
                OnPropertyChanged(nameof(CanAlign));
            }
        }
        private bool _externalLocked;

        private bool IsAutomaticMotion => SystemStatus.Equals("HOMING", StringComparison.OrdinalIgnoreCase) ||
                          SystemStatus.Equals("ALIGNING", StringComparison.OrdinalIgnoreCase) ||
                          SystemStatus.Equals("CALIBRATING", StringComparison.OrdinalIgnoreCase) ||
                          SystemStatus.Equals("TUNING", StringComparison.OrdinalIgnoreCase);

        private bool IsMotionActive => IsAutomaticMotion ||
                           SystemStatus.Equals("MOVING", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Send all alignment settings to hardware in one command
        /// </summary>
        private void SendAlignmentSettings()
        {
            var azDir = _azErrorRight ? 1 : 0;
            var alDir = _altErrorUp ? 1 : 0;
            var command = $"AzED:{_azErrorDeg},AzEM:{_azErrorMin},AzES:{_azErrorSec},AzDi:{azDir}," +
                          $"AlED:{_altErrorDeg},AlEM:{_altErrorMin},AlES:{_altErrorSec},AlDi:{alDir}\n";
            SendCommand(command);
            Logger.Info($"[MLAstro] Sent alignment settings: {command.TrimEnd()}");
        }

        /// <summary>
        /// Sends the error of ONE axis right away - used when the user presses Enter in a DMS box
        /// (no need to wait for the Align button). The firmware only writes FRAM and broadcasts to the other clients
        /// (the web UI); it does NOT move a motor.
        /// </summary>
        public void SendAlignmentAxisError(string axis)
        {
            if (string.Equals(axis, "alt", StringComparison.OrdinalIgnoreCase))
            {
                SendCommand($"AlED:{_altErrorDeg},AlEM:{_altErrorMin},AlES:{_altErrorSec},AlDi:{(_altErrorUp ? 1 : 0)}\n");
                Logger.Info("[MLAstro] Sent ALT alignment error (Enter)");
            }
            else
            {
                SendCommand($"AzED:{_azErrorDeg},AzEM:{_azErrorMin},AzES:{_azErrorSec},AzDi:{(_azErrorRight ? 1 : 0)}\n");
                Logger.Info("[MLAstro] Sent AZ alignment error (Enter)");
            }
        }

        /// <summary>
        /// Toggle between Modify and Done modes
        /// </summary>
        private void OnToggleModify()
        {
            IsAlignmentModifyMode = !IsAlignmentModifyMode;
        }

        /// <summary>
        /// Start editing alignment values - pause telemetry sync for these fields.
        /// </summary>
        public void StartEditingAlignment()
        {
            _isEditingAlignment = true;
        }

        /// <summary>
        /// End editing alignment values - resume telemetry sync for these fields.
        /// </summary>
        public void EndEditingAlignment()
        {
            _isEditingAlignment = false;
        }

        #endregion

        #region Commands

        // Speed Commands
        public ICommand SetSpeedCommand { get; }

        // Relative Step Commands
        public ICommand IncRelativeDegreesCommand { get; }
        public ICommand DecRelativeDegreesCommand { get; }
        public ICommand IncRelativeMinutesCommand { get; }
        public ICommand DecRelativeMinutesCommand { get; }
        public ICommand IncRelativeSecondsCommand { get; }
        public ICommand DecRelativeSecondsCommand { get; }

        // Movement Commands
        public ICommand MoveUpCommand { get; }
        public ICommand MoveDownCommand { get; }
        public ICommand MoveLeftCommand { get; }
        public ICommand MoveRightCommand { get; }
        public ICommand StopCommand { get; }
        public ICommand ForceStopCommand { get; }

        /// <summary>
        /// Raised when the operator stops the axes from the dock (STOP or FORCE STOP). The external
        /// correction session listens to it so a running TPPA session is cancelled instead of waiting for
        /// a controller whose motors were just stopped.
        /// </summary>
        public event EventHandler? ManualStopRequested;
        public ICommand ResetErrorCommand { get; }

        // Home Commands
        public ICommand SetHomeCommand { get; }
        public ICommand ReturnHomeCommand { get; }
        public ICommand ResetHomeCommand { get; }

        // Alignment Commands
        public ICommand AlignAzCommand { get; }
        public ICommand AlignAltCommand { get; }
        public ICommand AlignAllCommand { get; }
        public ICommand ToggleModifyCommand { get; }

        /// <summary>Clears the Alarm History table (a user action, it does not touch the error state).</summary>
        public ICommand ClearAlarmHistoryCommand { get; }

        #endregion

        [ImportingConstructor]
        public PolarAlignmentDockVM(IProfileService profileService, PluginSettings settings)
            : base(profileService)
        {
            // The dock title has to SAY which plugin this is: MLAstroRPA+TPPA has a dock with the same original
            // name "MLAstro RPA Control", and with both plugins installed they cannot be told apart by eye.
            Title = "MLAstro RPA Control (MLAstroRPA)";
            Logger.Info("[MLAstro] PolarAlignmentDockVM created");

            // Register this instance for cleanup during plugin teardown
            lock (_instanceLock)
            {
                _instance = this;
            }

            _settings = settings;

            // Use singleton instance to ensure we subscribe to the correct instance
            // MEF creates separate instances for different components, so we must use the singleton
            _serialService = SerialConnectionService.Instance;

            // Initialize Commands
#pragma warning disable CS0618 // NINA.RelayCommand is obsolete, but intentionally kept: it hooks CommandManager.RequerySuggested
            SetSpeedCommand = new RelayCommand(OnSetSpeed);

            IncRelativeDegreesCommand = new RelayCommand(_ => RelativeDegrees++);
            DecRelativeDegreesCommand = new RelayCommand(_ => RelativeDegrees--);
            IncRelativeMinutesCommand = new RelayCommand(_ => RelativeMinutes += 5);
            DecRelativeMinutesCommand = new RelayCommand(_ => RelativeMinutes -= 5);
            IncRelativeSecondsCommand = new RelayCommand(_ => RelativeSeconds += 5);
            DecRelativeSecondsCommand = new RelayCommand(_ => RelativeSeconds -= 5);

            // Movement commands are handled via Mouse events in code-behind
            MoveUpCommand = new RelayCommand(_ => { }); // Placeholder
            MoveDownCommand = new RelayCommand(_ => { });
            MoveLeftCommand = new RelayCommand(_ => { });
            MoveRightCommand = new RelayCommand(_ => { });
            StopCommand = new RelayCommand(_ => StopAllMovement());
            ForceStopCommand = new RelayCommand(_ => ForceStop());
            ResetErrorCommand = new RelayCommand(_ => SendCommand("ReER:1\n"));

            SetHomeCommand = new RelayCommand(_ => SendCommand("SetH:1\n"));
            ReturnHomeCommand = new RelayCommand(_ => SendCommand("RetH:1\n"));
            ResetHomeCommand = new RelayCommand(_ => SendCommand("RstH:1\n"));

            AlignAzCommand = new RelayCommand(_ => OnAlignAz(), _ => CanAlign);
            AlignAltCommand = new RelayCommand(_ => OnAlignAlt(), _ => CanAlign);
            AlignAllCommand = new RelayCommand(_ => OnAlignAll(), _ => CanAlign);
            ToggleModifyCommand = new RelayCommand(_ => OnToggleModify(), _ => CanModify);
            ClearAlarmHistoryCommand = new RelayCommand(_ => ClearAlarmHistoryRows());
#pragma warning restore CS0618

            // Subscribe to serial service events (using singleton)
            _serialService.PropertyChanged += OnSerialServicePropertyChanged;
            _serialService.TelemetryDataReceived += OnTelemetryDataReceived;
            _serialService.CompletionReceived += OnCompletionReceived;
            _serialService.ErrorStateChanged += OnErrorStateChanged;
            // Locks/unlocks the UI while TPPA (external plugin) takes/releases control.
            _serialService.AddExternalControlListener(active => IsExternalLocked = active);

            FirmwareVersion = _serialService.FirmwareVersion;
            UpdateApStatus();   // the "AP: Connected/Ready/Error" line for the current state
        }

        private void OnTelemetryDataReceived(object? sender, TelemetryDataEventArgs e)
        {
            if (e?.Data == null)
            {
                Logger.Warning("[MLAstro] OnTelemetryDataReceived: event data is null");
                return;
            }

            Logger.Info($"[MLAstro] ViewModel received telemetry - Status: {e.Data.Status}, AzPos: {e.Data.AzPosition}");

            // Update positions from home (AzPH/AlPH)
            AzPosition = e.Data.AzPosition;
            AltPosition = e.Data.AltPosition;

            // Update moved positions (Mpos - relative to alignment start)
            AzMovedPosition = e.Data.AzMovedPosition ?? "+0° 00' 00\"";
            AltMovedPosition = e.Data.AltMovedPosition ?? "+0° 00' 00\"";

            // Update system status with color
            SystemStatus = e.Data.Status;
            StatusForeground = e.Data.Status switch
            {
                "MOVING" => Brushes.Yellow,
                "HOMING" => Brushes.Cyan,
                "ALIGNING" => Brushes.Orange,
                "ALIGN_COMPLETED" => Brushes.LimeGreen,
                "HOME_COMPLETED" => Brushes.LimeGreen,
                "ERROR" => Brushes.Red,
                "READY" => Brushes.LimeGreen,
                _ => Brushes.White
            };

            Logger.Info($"[MLAstro] ViewModel updated - SystemStatus: {SystemStatus}, StatusForeground: {StatusForeground}");

            // Update current speed level (sync from hardware)
            if (e.Data.SpeedLevel > 0 && e.Data.SpeedLevel <= 5)
            {
                CurrentSpeed = e.Data.SpeedLevel;
            }

            // Update relative mode settings (sync from hardware)
            // Don't trigger command send by using backing field
            if (_isRelativeMode != e.Data.IsRelativeMode)
            {
                _isRelativeMode = e.Data.IsRelativeMode;
                RelativeOptionsVisibility = _isRelativeMode ? Visibility.Visible : Visibility.Collapsed;
                OnPropertyChanged(nameof(IsRelativeMode));
            }

            // Update relative values only when changed (skip if user is editing)
            if (e.Data.IsRelativeMode && !_isEditingRelativeValues && !IsDmsInputFocused())
            {
                RelativeDegrees = e.Data.RelativeDegrees;
                RelativeMinutes = e.Data.RelativeMinutes;
                RelativeSeconds = e.Data.RelativeSeconds;
            }

            // Update homed status from hardware (Read-Only)
            HomedStatus = e.Data.IsHomed ? "Yes" : "No";

            // Skip alignment sync if the user is editing OR modify mode is ON OR automated adjustment is ON
            if (!_isEditingAlignment && !IsDmsInputFocused() && !_isAlignmentModifyMode && !_isAutomatedAdjustment)
            {
                // Sync alignment directions from hardware (using flag to prevent sending command back)
                _isSyncingFromTelemetry = true;
                try
                {
                    // Use property setters to ensure UI binding updates
                    AzErrorRight = e.Data.AzDirection;
                    AltErrorUp = e.Data.AltDirection;
                }
                finally
                {
                    _isSyncingFromTelemetry = false;
                }

                // Sync alignment error values from hardware (only notifies UI when the value changes)
                AzErrorDeg = e.Data.AzErrorDegrees;
                AzErrorMin = e.Data.AzErrorMinutes;
                AzErrorSec = e.Data.AzErrorSeconds;
                AltErrorDeg = e.Data.AltErrorDegrees;
                AltErrorMin = e.Data.AltErrorMinutes;
                AltErrorSec = e.Data.AltErrorSeconds;
            }

            // Update steps display (calculate from position in degrees and steps/degree)
            if (e.Data.AzStepsPerDegree > 0)
            {
                var azSteps = (long)(e.Data.AzPositionDegrees * e.Data.AzStepsPerDegree);
                AzSteps = azSteps.ToString("N0");
            }
            else
            {
                AzSteps = "0";
            }

            if (e.Data.AltStepsPerDegree > 0)
            {
                var altSteps = (long)(e.Data.AltPositionDegrees * e.Data.AltStepsPerDegree);
                AltSteps = altSteps.ToString("N0");
            }
            else
            {
                AltSteps = "0";
            }

            // Speed values would need to be calculated from motor data
            // For now, keep placeholder values
            // AzOutSpeed, AltOutSpeed, AzMotorSpeed, AltMotorSpeed remain as initialized

            // The "STA: <icon> IP" line in the HeaderBar (firmware tokens WQu + STAi)
            UpdateStaStatus(e.Data.StaQuality, e.Data.StationIP);

            // The "AP: Connected/Ready/Error <IP>" line in the HeaderBar (firmware tokens APrd + APip)
            _apReady = e.Data.ApReady;
            _apIp = e.Data.ApIp ?? string.Empty;
            UpdateApStatus();
        }

        private void OnCompletionReceived(object? sender, string completionType)
        {
            switch (completionType)
            {
                case "AzAN":
                    Logger.Info("[MLAstro] Azimuth alignment completed");
                    break;
                case "AlAN":
                    Logger.Info("[MLAstro] Altitude alignment completed");
                    break;
                case "AAll":
                    Logger.Info("[MLAstro] All alignment completed");
                    break;
                case "HOME":
                    Logger.Info("[MLAstro] Home return completed");
                    // HomedStatus is now updated from telemetry (Home field)
                    break;
            }
        }

        private void OnErrorStateChanged(object? sender, DriverErrorState state)
        {
            if (state == null)
            {
                return;
            }

            // Guard: event may be raised from a background thread; marshal to UI thread once.
            if (Application.Current?.Dispatcher != null && !Application.Current.Dispatcher.CheckAccess())
            {
                Application.Current.Dispatcher.BeginInvoke(new Action(() => OnErrorStateChanged(sender, state)));
                return;
            }

            // Codes currently active (value 1 = WARNING, 2 = ERROR)
            var activeNow = new HashSet<string>(
                state.Codes.Where(kv => kv.Value == 1 || kv.Value == 2).Select(kv => kv.Key),
                StringComparer.OrdinalIgnoreCase);

            // Close rows whose code is no longer active -> mark the END time on the same row
            foreach (var row in _alarmHistory.Where(a => a.IsActive).ToList())
            {
                if (!activeNow.Contains(row.Code))
                {
                    row.ClearedAt = DateTime.Now;
                }
            }

            // Add a new row for each code that just became active
            foreach (var kv in state.Codes)
            {
                if (kv.Value != 1 && kv.Value != 2)
                {
                    continue;
                }

                // "Sys" is an aggregate indicator - skip it so we do not create a generic
                // "System error" row next to the specific driver code that actually caused it.
                if (kv.Key.Equals("Sys", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var alreadyActive = _alarmHistory.Any(a => a.Code.Equals(kv.Key, StringComparison.OrdinalIgnoreCase) && a.IsActive);
                if (alreadyActive)
                {
                    continue;
                }

                var alarm = new DriverAlarm(kv.Key, DriverErrorState.Describe(kv.Key), kv.Value);
                // Inserted AT THE TOP: the newest alarm sits on top, older alarms below.
                // (the DataGrid locks sorting with CanUserSortColumns=False, so this order is what shows.)
                _alarmHistory.Insert(0, alarm);
                NotifyAlarm(alarm);
            }

            // Keep history bounded - drop the OLDEST row (which now sits at the end of the list)
            while (_alarmHistory.Count > AlarmHistoryMaxEntries)
            {
                _alarmHistory.RemoveAt(_alarmHistory.Count - 1);
            }

            HasActiveErrors = state.HasErrors;
            HasActiveWarnings = state.HasWarnings;
            AlarmHistoryVisibility = _alarmHistory.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            // The axis hit a soft limit while the jog button was held -> lock the direction just pressed, release the button
            // once (send ":0") and send nothing further. See HandleJogRefused().
            //  - `AzSL`/`AlSL`: the global guard just STOPPED the axis at its limit (it notifies every motion source).
            //  - `RfJogAz`/`RfJogAl`: the axis was ALREADY at its limit and jog was pressed again. The firmware sets this bit
            //    only once AzSL/AlSL cleared, so the two never overlap (no duplicate alarm rows).
            if (IsCodeActive(state, "AzSL") || IsCodeActive(state, "RfJogAz")) HandleJogRefused("az");
            if (IsCodeActive(state, "AlSL") || IsCodeActive(state, "RfJogAl")) HandleJogRefused("alt");
        }

        /// <summary>True while the code is at WARNING (1) or ERROR (2) level.</summary>
        private static bool IsCodeActive(DriverErrorState state, string code)
        {
            return state.Codes.TryGetValue(code, out var value) && (value == 1 || value == 2);
        }

        /// <summary>
        /// The CLEAR button on the Alarm table only clears the DISPLAYED HISTORY. DIFFERENT from ClearAlarmHistory() (called on
        /// disconnect) - it leaves the active error/warning state of the device untouched.
        /// </summary>
        private void ClearAlarmHistoryRows()
        {
            // May be called from another thread -> it hops to the UI thread.
            if (Application.Current?.Dispatcher != null && !Application.Current.Dispatcher.CheckAccess())
            {
                Application.Current.Dispatcher.BeginInvoke(new Action(ClearAlarmHistoryRows));
                return;
            }

            _alarmHistory.Clear();
            AlarmHistoryVisibility = Visibility.Collapsed;
            Logger.Info("[MLAstro] Alarm history cleared by user");
        }

        private void NotifyAlarm(DriverAlarm alarm)
        {
            try
            {
                if (alarm.Severity == 2)
                {
                    Notification.ShowError($"MLAstro RPA: {alarm.Description}");
                }
                else
                {
                    Notification.ShowWarning($"MLAstro RPA: {alarm.Description}");
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"[MLAstro] Failed to show alarm notification: {ex.Message}");
            }
        }

        private void ClearAlarmHistory()
        {
            // Guard: may be called from a background thread (property-changed event)
            if (Application.Current?.Dispatcher != null && !Application.Current.Dispatcher.CheckAccess())
            {
                Application.Current.Dispatcher.BeginInvoke(new Action(ClearAlarmHistory));
                return;
            }

            _alarmHistory.Clear();
            HasActiveErrors = false;
            HasActiveWarnings = false;
            AlarmHistoryVisibility = Visibility.Collapsed;
        }

        private void OnSerialServicePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SerialConnectionService.IsConnected))
            {
                UpdateConnectionStatus();
                UpdateApStatus(); // switching transport (COM <-> wireless) changes the entry path
            }
            else if (e.PropertyName == nameof(SerialConnectionService.HandshakeStatus))
            {
                UpdateConnectionStatus();
            }
            else if (e.PropertyName == nameof(SerialConnectionService.LinkPath))
            {
                UpdateApStatus();
            }
            else if (e.PropertyName == nameof(SerialConnectionService.FirmwareVersion))
            {
                FirmwareVersion = _serialService.FirmwareVersion;
            }
        }

        private void UpdateConnectionStatus()
        {
            if (!_serialService.IsConnected)
            {
                ClearAlarmHistory();
            }

            if (_serialService.IsConnected && _serialService.HandshakeStatus == "OK!")
            {
                ConnectionStatusColor = Brushes.LimeGreen;
                ConnectionStatusText = "Connected";
                ControlsVisibility = Visibility.Visible;
            }
            else if (_serialService.IsConnected && _serialService.HandshakeStatus == "NO ANSWER")
            {
                ConnectionStatusColor = Brushes.Red;
                ConnectionStatusText = "Disconnected";
                SystemStatus = "DISCONNECTED";
                StatusForeground = Brushes.Red;
                ControlsVisibility = Visibility.Collapsed;
            }
            else if (_serialService.IsConnected)
            {
                ConnectionStatusColor = Brushes.Yellow;
                ConnectionStatusText = "Connecting...";
                ControlsVisibility = Visibility.Collapsed;
            }
            else
            {
                ConnectionStatusColor = Brushes.Gray;
                ConnectionStatusText = "Disconnected";
                SystemStatus = "DISCONNECTED";
                StatusForeground = Brushes.Gray;
                ControlsVisibility = Visibility.Collapsed;
            }
        }

        /// <summary>
        /// The "AP: ..." line in the HeaderBar. The DEVICE AP state comes from telemetry (tokens APrd/APip);
        /// whether the PC goes THROUGH the AP comes from the `link` the firmware reports in handshakeResult.
        /// </summary>
        private void UpdateApStatus()
        {
            // Until the device answers, the AP state is unknown.
            if (!_serialService.IsConnected)
            {
                ApIconGlyph = string.Empty;
                ApIconBrush = Brushes.Gray;
                ApStatusText = "-";
                return;
            }

            if (!_apReady)
            {
                ApIconGlyph = "\u274C";
                ApIconBrush = Brushes.Red;             // ❌ Error
                ApStatusText = string.Empty;
                return;
            }

            ApStatusText = string.IsNullOrWhiteSpace(_apIp) ? string.Empty : _apIp.Trim();
            if (string.Equals(_serialService.LinkPath, "AP", StringComparison.OrdinalIgnoreCase))
            {
                // <wireless mark> green: Connected - THIS PC (NINA) goes through the ESP32 hotspot.
                ApIconGlyph = "\U0001F6DC";
                ApIconBrush = Brushes.LimeGreen;
            }
            else
            {
                // <wireless mark> blue: Ready - the AP is up but the PC goes another way (STA / USB cable).
                ApIconGlyph = "\U0001F6DC";
                ApIconBrush = Brushes.DodgerBlue;
            }
        }

        /// <summary>
        /// The "STA: <icon> IP" line in the HeaderBar - glyph = link quality, COLOUR = CLIENT state:
        ///   glyph (token WQu): 0 = not joined to the router -> <cross> - 1 = router, no internet -> <bars+bang> -
        ///   2 = internet -> <bars> (text = the LAN IP of the device, "none" while not joined).
        ///   colour (LinkPath): green when THIS PC (NINA) goes through STA; blue when the PC goes
        ///   another way (USB cable "COM" or hotspot "AP"). The <cross> not-joined case stays red.
        ///   The icon must use a monochrome font (XAML: Segoe UI Symbol) for Foreground to apply.
        /// </summary>
        private void UpdateStaStatus(int staQuality, string? staIp)
        {
            var ip = string.IsNullOrWhiteSpace(staIp) ? string.Empty : staIp.Trim();

            // Client state: does THIS PC (NINA) currently go through this STA route.
            var clientViaSta = string.Equals(_serialService.LinkPath, "STA", StringComparison.OrdinalIgnoreCase);
            var clientBrush = clientViaSta ? Brushes.LimeGreen : Brushes.DodgerBlue;

            switch (staQuality)
            {
                case 1:
                    StaIconText = "\U0001F4F6\u2757";
                    StaIconBrush = clientBrush;
                    StaStatusText = ip.Length > 0 ? ip : "router only";
                    break;
                case 2:
                    StaIconText = "\U0001F4F6";
                    StaIconBrush = clientBrush;
                    StaStatusText = ip.Length > 0 ? ip : "connected";
                    break;
                default:
                    StaIconText = "\u274C";
                    StaIconBrush = Brushes.Red;
                    StaStatusText = "none";
                    break;
            }
        }

        private void UpdateStatusColor()
        {
            StatusForeground = SystemStatus.ToLower() switch
            {
                "error" => Brushes.Red,
                "moving" => Brushes.Yellow,
                "aligning" => Brushes.Cyan,
                "homing" => Brushes.Orange,
                _ => Brushes.White
            };
        }

        #region Command Implementations

        private void OnSetSpeed(object parameter)
        {
            if (parameter is int speed || int.TryParse(parameter?.ToString(), out speed))
            {
                CurrentSpeed = speed;
                SendCommand($"SLvl:{speed}\n");
            }
        }

        public void StartMoveUp()
        {
            UnblockJogAxis("alt");   // pressed again (opposite direction) -> unlock this axis arrow buttons
            if (IsRelativeMode)
            {
                SendRelativeMove("MAlU");
            }
            else
            {
                StartJogWatchdog("MAlU:1\n");
            }
        }

        public void StartMoveDown()
        {
            UnblockJogAxis("alt");
            if (IsRelativeMode)
            {
                SendRelativeMove("MAlD");
            }
            else
            {
                StartJogWatchdog("MAlD:1\n");
            }
        }

        public void StartMoveLeft()
        {
            UnblockJogAxis("az");
            if (IsRelativeMode)
            {
                SendRelativeMove("MAzL");
            }
            else
            {
                StartJogWatchdog("MAzL:1\n");
            }
        }

        public void StartMoveRight()
        {
            UnblockJogAxis("az");
            if (IsRelativeMode)
            {
                SendRelativeMove("MAzR");
            }
            else
            {
                StartJogWatchdog("MAzR:1\n");
            }
        }

        public void StopAllMovement()
        {
            StopJogMovement();
            SendCommand("STOP:1\n");
            // If TPPA currently holds external control, tell it to stop the polar alignment right away.
            if (_serialService.IsExternalControlActive) _serialService.NotifyExternalStop("MLAstro STOP pressed");
            // Broker session: tell the controller so it sends the cancel to TPPA right away.
            ManualStopRequested?.Invoke(this, EventArgs.Empty);
        }

        public void ForceStop()
        {
            // FORCE-STOP must also end any active jog watchdog. Otherwise a held jog keeps re-sending
            // ":1" right after the ESTOP, the firmware re-arms the far move(+-1e9) and the motor
            // simply restarts - which looked like "FORCE-STOP does not stop the motor".
            StopJogMovement();
            SendCommand("ESTOP:1\n");
            if (_serialService.IsExternalControlActive) _serialService.NotifyExternalStop("MLAstro FORCE-STOP pressed");
            // Broker session: FORCE-STOP has to end the TPPA session as well.
            ManualStopRequested?.Invoke(this, EventArgs.Empty);
        }

        public void StopJogMovement()
        {
            if (!IsRelativeMode)
            {
                StopJogWatchdog();
            }
        }

        private void StartJogWatchdog(string command)
        {
            lock (_jogLock)
            {
                _currentJogCommand = command;

                if (_jogWatchdogTimer == null)
                {
                    _jogWatchdogTimer = new System.Timers.Timer(250); // Send every 250ms
                    _jogWatchdogTimer.Elapsed += (s, e) =>
                    {
                        string? toSend;
                        lock (_jogLock)
                        {
                            // Re-check under the lock so a Stop() that already cleared the command
                            // can never be followed by a stray ":1" from an in-flight timer tick.
                            toSend = (!string.IsNullOrEmpty(_currentJogCommand) && _serialService.IsConnected)
                                ? _currentJogCommand
                                : null;
                        }
                        if (toSend != null)
                        {
                            _serialService.Send(toSend);
                        }
                    };
                }

                _jogWatchdogTimer.Start();
            }
            SendCommand(command); // Send immediately first time
            Logger.Info($"[MLAstro] Started Jog watchdog: {command.TrimEnd()}");
        }

        private void StopJogWatchdog()
        {
            string? stopCmd = null;
            lock (_jogLock)
            {
                _jogWatchdogTimer?.Stop();
                if (!string.IsNullOrEmpty(_currentJogCommand))
                {
                    // Capture the stop command and CLEAR the active jog BEFORE sending, so a queued
                    // watchdog tick can never deliver a stray ":1" after our ":0" / STOP / ESTOP.
                    stopCmd = _currentJogCommand.Replace(":1", ":0");
                    _currentJogCommand = null;
                }
            }

            if (stopCmd != null)
            {
                SendCommand(stopCmd);
                Logger.Info($"[MLAstro] Stopped Jog: {stopCmd.TrimEnd()}");
            }
        }

        private void SendRelativeMove(string axis)
        {
            // First, send relative angle setup
            SendCommand($"ReDe:{RelativeDegrees}\n");
            SendCommand($"ReAM:{RelativeMinutes}\n");
            SendCommand($"ReAS:{RelativeSeconds}\n");

            // Then send move command (just once, no watchdog needed)
            SendCommand($"{axis}:1\n");
            Logger.Info($"[MLAstro] Relative move: {axis} - {RelativeDegrees}° {RelativeMinutes}' {RelativeSeconds}\"");
        }

        private void SendMoveCommand(string command, bool start)
        {
            // Deprecated - now using StartMove* methods
            var cmd = $"{command}:{(start ? 1 : 0)}\n";
            SendCommand(cmd);
        }

        private void SendCommand(string command)
        {
            if (_serialService.IsConnected)
            {
                _serialService.Send(command);
                Logger.Info($"[MLAstro] Sent command: {command.TrimEnd()}");
            }
            else
            {
                // This branch used to be SILENT => the buttons still worked but nothing reached the firmware,
                // which is hard to diagnose (e.g. with two MLAstro plugins installed, the dock of the plugin that does NOT own the COM port).
                Logger.Warning($"[MLAstro] Command dropped - link not connected: {command.TrimEnd()}");
            }
        }

        private void OnAlignAz()
        {
            EndEditingAlignment();

            var direction = AzErrorRight ? 1 : 0;
            var command = $"AzED:{AzErrorDeg},AzEM:{AzErrorMin},AzES:{AzErrorSec},AzAN:1\n";
            SendCommand(command);
        }

        private void OnAlignAlt()
        {
            EndEditingAlignment();

            var direction = AltErrorUp ? 1 : 0;
            var command = $"AlED:{AltErrorDeg},AlEM:{AltErrorMin},AlES:{AltErrorSec},AlAN:1\n";
            SendCommand(command);
        }

        private void OnAlignAll()
        {
            EndEditingAlignment();

            var command = $"AzED:{AzErrorDeg},AzEM:{AzErrorMin},AzES:{AzErrorSec}," +
                         $"AlED:{AltErrorDeg},AlEM:{AltErrorMin},AlES:{AltErrorSec},AAll:1\n";
            SendCommand(command);
        }

        #endregion

        #region Cleanup / Dispose

        public void Cleanup()
        {
            Dispose();
        }

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
                Logger.Info("[MLAstro] PolarAlignmentDockVM disposing...");

                // Stop jog watchdog timer
                StopJogWatchdog();
                if (_jogWatchdogTimer != null)
                {
                    _jogWatchdogTimer.Dispose();
                    _jogWatchdogTimer = null;
                }

                // Stop the jog auto-unblock timer
                if (_jogUnblockTimer != null)
                {
                    _jogUnblockTimer.Stop();
                    _jogUnblockTimer = null;
                }

                // Unsubscribe from serial service events
                if (_serialService != null)
                {
                    _serialService.PropertyChanged -= OnSerialServicePropertyChanged;
                    _serialService.TelemetryDataReceived -= OnTelemetryDataReceived;
                    _serialService.CompletionReceived -= OnCompletionReceived;
                    _serialService.ErrorStateChanged -= OnErrorStateChanged;
                }

                // Clear static instance
                lock (_instanceLock)
                {
                    if (ReferenceEquals(_instance, this))
                    {
                        _instance = null;
                    }
                }

                Logger.Info("[MLAstro] PolarAlignmentDockVM disposed");
            }

            _disposed = true;
        }

        ~PolarAlignmentDockVM()
        {
            Dispose(false);
        }

        #endregion
    }
}
