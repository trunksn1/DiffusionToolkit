using System;
using System.Globalization;
using System.Windows.Data;

namespace Diffusion.Toolkit.Converters;

/// <summary>
/// Formats a UTC <see cref="DateTime"/> as the local wall-clock time (HH:mm).
/// Used by the CivitAI calendar so scheduled/published hours match the site.
/// </summary>
public class LocalTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is DateTime dt)
        {
            var utc = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
            return utc.ToLocalTime().ToString("HH:mm");
        }
        return string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
