using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Cockpit.App.Services;
using Cockpit.App.Theming;
using Cockpit.Core.Help;

namespace Cockpit.App.Controls;

// AC-1033: the one `?` the app and every plugin share, hiding itself when its target is not there. Why the
// SDK draws it rather than each plugin: Help > Extending Cockpit > Shipping documentation.
internal sealed class HelpHint : Button
{
    public HelpHint(HelpService help, HelpAddress address, string? label = null, string? origin = null)
    {
        Classes.Add("Subtle");
        IsVisible = help.Contains(address);
        VerticalAlignment = VerticalAlignment.Center;
        Foreground = ThemeBrush.Resolve("CockpitAccentBrush", "#2563eb");
        FontSize = 12;

        // Two shapes for three places: a bare mark behind a label or heading, a worded link inside a sentence.
        if (label is { Length: > 0 })
        {
            Content = $"{label} →";
            Padding = new Thickness(0);
        }
        else
        {
            Content = "?";
            Padding = new Thickness(0);
            Width = 16;
            Height = 16;
            CornerRadius = new CornerRadius(8);
            HorizontalContentAlignment = HorizontalAlignment.Center;
            VerticalContentAlignment = VerticalAlignment.Center;
            Margin = new Thickness(4, 0, 0, 0);
            BorderThickness = new Thickness(1);
            BorderBrush = ThemeBrush.Resolve("CockpitHairlineBrush", "#2a2f39");
        }

        // Hovering says where it goes, so following one is a decision rather than a guess.
        ToolTip.SetTip(this, help.Describe(address));
        Click += (_, _) => help.Open(address, origin ?? "a “?” elsewhere in the app");
    }
}
