using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace FileExplorer.Converters;

public sealed partial class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var b = value is bool v && v;
        if (parameter is string s && string.Equals(s, "Invert", StringComparison.OrdinalIgnoreCase))
        {
            b = !b;
        }
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed partial class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var isNull = value is null;
        if (parameter is string s && string.Equals(s, "Invert", StringComparison.OrdinalIgnoreCase))
        {
            isNull = !isNull;
        }
        return isNull ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed partial class ActivePaneHighlightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var isActive = value is bool v && v;
        return isActive
            ? Application.Current.Resources["AccentFillColorDefaultBrush"]
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed partial class TagColorToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var color = (value as string) switch
        {
            "Red" => Windows.UI.Color.FromArgb(255, 232, 17, 35),
            "Orange" => Windows.UI.Color.FromArgb(255, 255, 140, 0),
            "Yellow" => Windows.UI.Color.FromArgb(255, 255, 185, 0),
            "Green" => Windows.UI.Color.FromArgb(255, 16, 137, 62),
            "Blue" => Windows.UI.Color.FromArgb(255, 0, 99, 177),
            "Purple" => Windows.UI.Color.FromArgb(255, 136, 23, 152),
            _ => (Windows.UI.Color?)null,
        };

        return new Microsoft.UI.Xaml.Media.SolidColorBrush(color ?? Microsoft.UI.Colors.Transparent);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed partial class SyncRoleToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var color = value switch
        {
            FileExplorer.Models.SyncRole.Source => Windows.UI.Color.FromArgb(255, 255, 140, 0),
            FileExplorer.Models.SyncRole.Target => Windows.UI.Color.FromArgb(255, 16, 137, 62),
            _ => Microsoft.UI.Colors.Transparent,
        };

        return new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed partial class WatchedToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var color = value is true
            ? Windows.UI.Color.FromArgb(255, 79, 195, 247)
            : Microsoft.UI.Colors.Transparent;

        return new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed partial class UsedPercentToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var color = value switch
        {
            double v when v > 90 => Windows.UI.Color.FromArgb(255, 232, 17, 35), // red
            double v when v > 80 => Windows.UI.Color.FromArgb(255, 255, 140, 0), // orange
            double v when v > 60 => Windows.UI.Color.FromArgb(255, 0, 99, 177),  // blue
            double => Windows.UI.Color.FromArgb(255, 16, 137, 62),               // green
            _ => Microsoft.UI.Colors.Gray,
        };

        return new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
