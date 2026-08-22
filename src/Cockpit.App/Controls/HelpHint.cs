using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Cockpit.App.Services;
using Cockpit.App.Theming;
using Cockpit.Core.Help;

namespace Cockpit.App.Controls;

// The standard "open the help about this" affordance (AC-1033): one `?`, drawn once, wherever the question
// comes up. The SDK hands this out rather than letting each plugin draw its own, because twenty-seven
// plugins drawing their own is twenty-seven icons, sizes and behaviours for the same promise.
//
// It hides itself when its target is not there. A question mark that opens nothing is worse than no question
// mark, so a caller can point at a page unconditionally — an uninstalled plugin or a renamed section simply
// leaves no mark behind rather than a promise that breaks when taken up.
internal sealed class HelpHint : Button
{
    public HelpHint(HelpService help, HelpAddress address, string? label = null, string? origin = null)
    {
        Classes.Add("Subtle");
        IsVisible = help.Contains(address);
        VerticalAlignment = VerticalAlignment.Center;
        Foreground = ThemeBrush.Resolve("CockpitAccentBrush", "#2563eb");
        FontSize = 12;

        // Two shapes for three places. A bare mark sits behind a field label or in a heading, where the words
        // around it already say what it is about; a worded link carries its own sentence, for an error message
        // or a paragraph where a floating `?` would have nothing to sit beside.
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
