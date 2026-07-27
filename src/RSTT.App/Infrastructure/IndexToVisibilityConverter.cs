using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace RSTT.App.Infrastructure;

public sealed class IndexToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var expected = int.TryParse(parameter?.ToString(), out var index) ? index : -1;
        return value is int actual && actual == expected ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        System.Windows.Data.Binding.DoNothing;
}
