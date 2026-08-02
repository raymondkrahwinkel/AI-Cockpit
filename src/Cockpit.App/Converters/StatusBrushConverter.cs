using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Cockpit.App.Converters;

// Resolves a theme brush resource key (e.g. `"CockpitStatusBusyBrush"`, as produced by
// `ViewModels.SessionViewModel.SessionStatusBrushKey`) to the actual
// `IBrush` from `Application.Resources`, so the sidebar/grid status dot
// can bind directly to a view-model string without a codebehind lookup.
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
