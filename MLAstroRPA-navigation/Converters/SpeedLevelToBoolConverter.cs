using System;
using System.Globalization;
using System.Windows.Data;

namespace MLAstro_Robotic_Polar_Alignment.Converters
{
    /// <summary>
    /// Converts CurrentSpeed and CommandParameter to bool for button selection state.
    /// Returns true if the current speed matches the parameter.
    /// </summary>
    public class SpeedLevelToBoolConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length < 2)
                return false;

            if (values[0] is int currentSpeed && values[1] is string paramStr)
            {
                if (int.TryParse(paramStr, out int paramSpeed))
                {
                    return currentSpeed == paramSpeed;
                }
            }

            return false;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
