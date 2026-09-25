using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace MLAstroRPA.Dockables
{
    /// <summary>Mức độ của một dòng System log (giống class CSS của Web UI).</summary>
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
    /// Một dòng trong bảng System log ở tab CONNECTION — mô phỏng đúng bảng System Log của Web UI:
    /// có timestamp phía trước, tô màu theo từ khóa, dòng mới nhất nằm trên cùng.
    /// Chỉ chứa thông báo của firmware/plugin (không có TX/RX frame thô).
    /// </summary>
    public class SystemLogEntry
    {
        public SystemLogEntry(string message, SystemLogLevel level, DateTime timestamp)
        {
            // Web UI dùng toLocaleTimeString() → đúng định dạng giờ NGẮN của locale hệ thống
            // (vi-VN: "17:07:51" 24 giờ; en-US: "5:07:51 PM"). Dùng pattern "t" của .NET — cũng lấy
            // từ locale hệ thống — để cột thời gian của plugin khớp y hệt bảng log của web.
            Time = timestamp.ToString("t", CultureInfo.CurrentCulture);
            Message = message ?? string.Empty;
            Level = level;
        }

        /// <summary>Thời điểm, định dạng giống Web UI (vd "11:08:06 PM").</summary>
        public string Time { get; }

        public string Message { get; }

        public SystemLogLevel Level { get; }

        /// <summary>Dòng hiển thị đầy đủ: "[thời gian] nội dung".</summary>
        public string DisplayText => $"[{Time}] {Message}";

        /// <summary>Đậm như Web UI cho các dòng critical / apply / reboot-required.</summary>
        public FontWeight FontWeightValue =>
            (Level == SystemLogLevel.Critical || Level == SystemLogLevel.Apply || Level == SystemLogLevel.RebootRequired)
                ? FontWeights.Bold
                : FontWeights.Normal;

        /// <summary>
        /// Màu chữ cho một mức log, CHỌN THEO NỀN (tối/sáng) để luôn tương phản.
        /// Nền tối dùng biến thể sáng hơn của đúng tông màu Web UI (đỏ/cam/xanh...) vì
        /// Red/DarkOrange/Green gốc bị tối, khó đọc trên nền đen của theme NINA.
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
            // Info: trắng trên nền tối, đen trên nền sáng → luôn tương phản (trước đây hard-code đen).
            _ => darkBackground ? Brushes.White : Brushes.Black
        };

        /// <summary>Màu nhấn cho cụm "(Backlash applied)" — giống span.log-backlash của Web UI.</summary>
        public static Brush HighlightBrush(bool darkBackground)
            => darkBackground ? new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x4A)) : Brushes.Orange;
    }
}
