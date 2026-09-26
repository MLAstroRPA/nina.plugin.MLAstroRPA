using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace MLAstroRPA.Dockables
{
    /// <summary>Level of a system log line (mirrors the Web UI CSS class).</summary>
    public enum SystemLogLevel
    {
        Info,
        Success,
        Warning,
        Critical,
        Apply,
        RebootRequired,
        Reset
    }

    /// <summary>
    /// One line of the System log table on the CONNECTION tab - it mirrors the Web UI System Log:
    /// a timestamp in front, colour by keyword, newest line on top.
    /// Firmware/plugin messages only (no raw TX/RX frames).
    /// </summary>
    public class SystemLogEntry
    {
        public SystemLogEntry(string message, SystemLogLevel level, DateTime timestamp)
        {
            // The Web UI uses toLocaleTimeString() -> the SHORT time format of the system locale
            // (vi-VN: "17:07:51" 24-hour; en-US: "5:07:51 PM"). The .NET "t" pattern - also taken from
            // the system locale - keeps the plugin time column identical to the web log table.
            Time = timestamp.ToString("t", CultureInfo.CurrentCulture);
            Message = message ?? string.Empty;
            Level = level;
        }

        /// <summary>Timestamp, formatted like the Web UI (e.g. "11:08:06 PM").</summary>
        public string Time { get; }

        public string Message { get; }

        public SystemLogLevel Level { get; }

        /// <summary>Full display line: "[time] content".</summary>
        public string DisplayText => $"[{Time}] {Message}";

        /// <summary>Bold like the Web UI for critical / apply / reboot-required lines.</summary>
        public FontWeight FontWeightValue =>
            (Level == SystemLogLevel.Critical || Level == SystemLogLevel.Apply || Level == SystemLogLevel.RebootRequired)
                ? FontWeights.Bold
                : FontWeights.Normal;

        /// <summary>
        /// Text colour for a log level, PICKED FOR THE BACKGROUND (dark/light) so it always contrasts.
        /// A dark background uses a lighter variant of the exact Web UI hue (red/orange/green...) because
        /// plain Red/DarkOrange/Green is too dark to read on the black NINA theme.
        /// </summary>
        public static Brush BrushForLevel(SystemLogLevel level, bool darkBackground) => level switch
        {
            SystemLogLevel.Critical => darkBackground ? new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)) : Brushes.Red,
            SystemLogLevel.Warning => darkBackground ? new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x4A)) : Brushes.DarkOrange,
            SystemLogLevel.Reset => darkBackground ? new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x4A)) : Brushes.DarkOrange,
            SystemLogLevel.Apply => darkBackground ? new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x4A)) : Brushes.Orange,
            SystemLogLevel.RebootRequired => darkBackground
                ? new SolidColorBrush(Color.FromRgb(0x64, 0xB5, 0xF6))
                : new SolidColorBrush(Color.FromRgb(0x19, 0x76, 0xD2)),
            SystemLogLevel.Success => darkBackground ? new SolidColorBrush(Color.FromRgb(0x7C, 0xD9, 0x7C)) : Brushes.Green,
            // Info: white on a dark background, black on a light one -> always contrasts (it used to be hard-coded black).
            _ => darkBackground ? Brushes.White : Brushes.Black
        };

        /// <summary>Accent colour for the "(Backlash applied)" phrase - like the Web UI span.log-backlash.</summary>
        public static Brush HighlightBrush(bool darkBackground)
            => darkBackground ? new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x4A)) : Brushes.Orange;
    }
}
