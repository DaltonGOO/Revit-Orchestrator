using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace RevitOrchestrator.UI.Converters;

/// <summary>
/// Converts event type "completed" to Visible, all others to Collapsed.
/// Used to show checkboxes only for completed events.
/// </summary>
public sealed class CompletedToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string eventType)
            return eventType == "completed" ? Visibility.Visible : Visibility.Collapsed;
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
