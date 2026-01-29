using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace RevitOrchestrator.UI.Converters;

/// <summary>
/// Converts a message role to horizontal alignment and background color.
/// User messages align right, assistant/system messages align left.
/// </summary>
public sealed class MessageRoleToAlignmentConverter : IValueConverter, IMultiValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var role = value as string ?? "";

        if (parameter is string param && param == "background")
        {
            return role switch
            {
                "user" => new SolidColorBrush(Color.FromRgb(0x1a, 0x73, 0xe8)),
                "assistant" => new SolidColorBrush(Color.FromRgb(0xf1, 0xf3, 0xf4)),
                "system" => new SolidColorBrush(Color.FromRgb(0xff, 0xf3, 0xe0)),
                _ => new SolidColorBrush(Color.FromRgb(0xf1, 0xf3, 0xf4)),
            };
        }

        return role switch
        {
            "user" => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Left,
        };
    }

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length > 0)
            return Convert(values[0], targetType, parameter, culture);
        return new SolidColorBrush(Colors.Transparent);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
