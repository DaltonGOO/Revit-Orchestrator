using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace RevitOrchestrator.UI.Converters;

/// <summary>
/// Converts null or empty string to Collapsed, otherwise Visible.
/// </summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null)
            return Visibility.Collapsed;
        if (value is string s && string.IsNullOrEmpty(s))
            return Visibility.Collapsed;
        return Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
