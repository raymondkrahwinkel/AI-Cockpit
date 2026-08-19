using System.Globalization;
using Avalonia.Data.Converters;
using Material.Icons;

namespace Cockpit.App.Converters;

// AC-953: the assistant header's Dock/Undock button — the one control that differs between its two hosts.
// Same shape as ReadAloudIconConverter next door, and for the same reason: the header is a strip of icons, so
// the button says what it does with an icon, and the tooltip is what says which way it currently is.
public sealed class DockToggleConverter : IValueConverter
{
    public static readonly DockToggleConverter Icon = new();

    public static readonly DockToggleConverter Tip = new() { _isTooltip = true };

    private bool _isTooltip;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isDocked = value is true;

        if (!_isTooltip)
        {
            return isDocked ? MaterialIconKind.DockWindow : MaterialIconKind.DockRight;
        }

        return isDocked
            ? "Undock into a floating window"
            : "Dock into the panel on the right";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("The dock toggle is display-only; the command writes the state itself.");
}
