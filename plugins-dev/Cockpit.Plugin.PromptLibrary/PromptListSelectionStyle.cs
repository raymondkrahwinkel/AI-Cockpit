using Avalonia.Controls;
using Avalonia.Media;

namespace Cockpit.Plugin.PromptLibrary;

/// <summary>
/// Gives a prompt <see cref="ListBox"/> a clearly visible selected-item highlight (the default Fluent
/// selection was too faint to see which template is active). It overrides the Fluent selection-background
/// resources on the list with the host's accent colour — resolved from the app theme once the list is attached,
/// with a sensible fallback — so the current item reads at a glance in both the full dialog and the quick pick.
/// </summary>
internal static class PromptListSelectionStyle
{
    // A translucent coral matching the app's accent — used until (and if) the real theme brush is resolved.
    private static readonly IBrush Fallback = new SolidColorBrush(Color.FromArgb(0x66, 0xE2, 0x79, 0x5A));

    public static void Apply(ListBox list)
    {
        _Set(list, Fallback);
        list.AttachedToVisualTree += (_, _) =>
        {
            var brush = list.TryFindResource("CockpitAccentBrush", out var resource) && resource is IBrush accent
                ? accent
                : Fallback;
            _Set(list, brush);
        };
    }

    private static void _Set(ListBox list, IBrush brush)
    {
        list.Resources["ListBoxItemBackgroundSelected"] = brush;
        list.Resources["ListBoxItemBackgroundSelectedPointerOver"] = brush;
        list.Resources["ListBoxItemBackgroundSelectedPressed"] = brush;
    }
}
