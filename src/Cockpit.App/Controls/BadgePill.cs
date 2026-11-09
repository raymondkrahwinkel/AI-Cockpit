using Avalonia.Controls;
using Cockpit.App.Theming;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.Controls;

// How a SideMenuButtonBadge looks as a count pill; the sidebar's plugin rows and the companion strip draw the same one.
internal static class BadgePill
{
    public static Border Create(out TextBlock text)
    {
        text = new TextBlock { FontSize = 10.5 };
        return new Border
        {
            CornerRadius = new Avalonia.CornerRadius(8),
            BorderThickness = new Avalonia.Thickness(1),
            Padding = new Avalonia.Thickness(6, 0),
            Child = text,
        };
    }

    public static void Render(SideMenuButtonBadge badge, Border pill, TextBlock label)
    {
        var text = badge.ToDisplayText();
        label.Text = text;
        // The default tone is the mockups' plain `.cnt` (the waiting amber), Success is `.cnt.ok`.
        (pill.Background, pill.BorderBrush, label.Foreground) = badge.Tone switch
        {
            SideMenuButtonBadgeTone.Success =>
                (ThemeBrush.Resolve("CockpitStatusDoneChipTintBrush", "#145AA576"), ThemeBrush.Resolve("CockpitStatusDoneRingTintBrush", "#1F5AA576"),
                    ThemeBrush.Resolve("CockpitStatusDoneBrush", "#5AA576")),
            SideMenuButtonBadgeTone.Neutral =>
                (ThemeBrush.Resolve("CockpitInsetBgBrush", "#16181d"), ThemeBrush.Resolve("CockpitHairlineBrush", "#2a2f39"),
                    ThemeBrush.Resolve("CockpitTextSecondaryBrush", "#9aa3b2")),
            _ =>
                (ThemeBrush.Resolve("CockpitStatusWaitingChipTintBrush", "#14E0A33E"), ThemeBrush.Resolve("CockpitStatusWaitingRingTintBrush", "#1FE0A33E"),
                    ThemeBrush.Resolve("CockpitStatusWaitingBrush", "#E0A33E")),
        };
        pill.IsVisible = text.Length > 0;
    }
}
