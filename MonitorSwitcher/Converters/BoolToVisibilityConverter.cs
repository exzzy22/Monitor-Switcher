using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MonitorSwitcher.Converters;

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool boolValue = value is bool b && b;

        // If parameter is "Invert", reverse the logic
        if (parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase))
            boolValue = !boolValue;

        return boolValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is Visibility v && v == Visibility.Visible;
    }
}
