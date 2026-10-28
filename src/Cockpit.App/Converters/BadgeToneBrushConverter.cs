using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.Converters;

internal sealed class BadgeToneBrushConverter : IValueConverter
{
    public static BadgeToneBrushConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var suffix = parameter as string ?? string.Empty;
        var key = value switch
        {
            null => "CockpitTextFaintBrush",
            SideMenuButtonBadgeTone.Success => "CockpitStatusDone" + suffix + "Brush",
            SideMenuButtonBadgeTone.Neutral => suffix switch
            {
                "ChipTint" => "CockpitInsetBgBrush",
                "RingTint" => "CockpitHairlineBrush",
                _ => "CockpitTextSecondaryBrush",
            },
            _ => "CockpitStatusWaiting" + suffix + "Brush",
        };
        return Application.Current?.TryFindResource(key, out var brush) == true ? brush : null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
