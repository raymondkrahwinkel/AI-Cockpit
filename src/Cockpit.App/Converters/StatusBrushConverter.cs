using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Styling;

namespace Cockpit.App.Converters;

/// <summary>
/// Resolves a theme brush resource key (e.g. <c>"CockpitStatusBusyBrush"</c>, as produced by
/// <see cref="ViewModels.SessionViewModel.SessionStatusBrushKey"/>) to the actual
/// <see cref="IBrush"/> from <see cref="Application.Resources"/>, so the sidebar/grid status dot
/// can bind directly to a view-model string without a codebehind lookup.
/// </summary>
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
