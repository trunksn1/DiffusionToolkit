using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Diffusion.Toolkit.Converters;

public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // ConverterParameter="invert" flips the logic: visible when null.
        var visibleWhenNull = parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase);
        return (value != null) ^ visibleWhenNull ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
