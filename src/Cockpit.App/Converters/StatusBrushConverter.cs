using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Cockpit.App.Converters;

// Resolves the view-model's theme brush key to an IBrush, letting status dots bind without code-behind lookup.
public sealed class StatusBrushConverter : IValueConverter
{
    public static readonly StatusBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string key && Application.Current is { } app && app.TryFindResource(key, out var resource))
        {
            return resource;
        }

        return Brushes.Transparent;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
