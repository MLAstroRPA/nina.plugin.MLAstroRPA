using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace MLAstroRPA.Plugin
{
    /// <summary>
    /// Paints a status line by what it says:
    /// green when the link is up ("Connected: ...") or the handshake answered "OK", orange when the link is
    /// down ("Disconnected"), red when the connect failed ("Connect failed: ...") or the handshake did not
    /// come back in time ("TIME OUT"). Anything else stays gray.
    /// </summary>
    public class StatusBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => BrushFor(value as string);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();

        public static Brush BrushFor(string? status)
        {
            if (string.IsNullOrWhiteSpace(status))
            {
                return Brushes.Gray;
            }

            if (status.Contains("failed", StringComparison.OrdinalIgnoreCase)
                || status.Contains("TIME OUT", StringComparison.OrdinalIgnoreCase)
                || status.Contains("NO ANSWER", StringComparison.OrdinalIgnoreCase))
            {
                return Brushes.Red;
            }

            // "Disconnected" must be told apart from "Connected" (the former contains the latter).
            if (status.Contains("Disconnected", StringComparison.OrdinalIgnoreCase))
            {
                return Brushes.Orange;
            }

            if (status.Contains("OK", StringComparison.OrdinalIgnoreCase)
                || status.Contains("Connected", StringComparison.OrdinalIgnoreCase))
            {
                return Brushes.Green;
            }

            return Brushes.Gray;
        }
    }
}
