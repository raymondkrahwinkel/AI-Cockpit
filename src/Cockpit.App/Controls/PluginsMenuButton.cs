using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Cockpit.App.Theming;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.Controls;

// AC-937/AC-1013: sidebar's collapsed-plugins launcher, opening a plain Flyout (not MenuFlyout, which can't
// host a control's own gear/badge) over the PluginLauncherButton/PluginSectionControl instances that would
// otherwise be drawn directly. Accent dot sums every collapsed badge (AC-516) rather than a count, since mixed units don't add to a meaningful number.
internal sealed class PluginsMenuButton : Button
{
    protected override Type StyleKeyOverride => typeof(Button);

    private readonly IReadOnlyList<SideMenuButtonBadge> _badges;
    private readonly Border _dot;

    public PluginsMenuButton(IReadOnlyList<Control> collapsedControls, IReadOnlyList<SideMenuButtonBadge> badges)
    {
        _badges = badges;
        Name = "PluginsMenuButton";
        HorizontalAlignment = HorizontalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Left;
        ToolTip.SetTip(this, "Plugins collapsed out of the sidebar — pin one in the plugin manager to show it here instead");

        _dot = new Border
        {
            Width = 6,
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = ThemeBrush.Resolve("CockpitAccentBrush", "#2563eb"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
            IsVisible = false,
        };

        var content = new DockPanel();
        DockPanel.SetDock(_dot, Dock.Right);
        content.Children.Add(_dot);
        content.Children.Add(new TextBlock { Text = "Plugins ›", VerticalAlignment = VerticalAlignment.Center });
        Content = content;

        // AC-937 (herzien): a flush list with hairline separators, matching the sidebar's own look — not the
        // "Subtle" chromeless class's default 2px padding, which would collapse each row to a sliver.
        var hairline = ThemeBrush.Resolve("CockpitHairlineBrush", "#2a2f39");
        var flyoutPanel = new StackPanel { Spacing = 0 };
        for (var i = 0; i < collapsedControls.Count; i++)
        {
            if (i > 0)
            {
                flyoutPanel.Children.Add(new Border { Height = 1, Background = hairline });
            }

            if (collapsedControls[i] is Button button)
            {
                button.Classes.Add("Subtle");
                button.Padding = new Thickness(8, 8);
                button.CornerRadius = new CornerRadius(0);
            }

            flyoutPanel.Children.Add(collapsedControls[i]);
        }

        Flyout = new Flyout { Placement = PlacementMode.Right, Content = flyoutPanel };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        foreach (var badge in _badges)
        {
            badge.Changed += _OnBadgeChanged;
        }

        // A badge may have changed between construction and this button landing in the tree, same reasoning as
        // PluginLauncherButton's own re-render on attach.
        _RenderDot();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        foreach (var badge in _badges)
        {
            badge.Changed -= _OnBadgeChanged;
        }

        base.OnDetachedFromVisualTree(e);
    }

    private void _OnBadgeChanged() => Dispatcher.UIThread.Post(_RenderDot);

    private void _RenderDot() => _dot.IsVisible = ShouldShowDot(_badges);

    // The dot rule, pulled out as a pure function so it is testable without a visual tree: on while any collapsed
    // badge has something to say (AC-937) — never a summed number, since the badges are not the same unit.
    internal static bool ShouldShowDot(IReadOnlyList<SideMenuButtonBadge> badges) =>
        badges.Any(badge => badge.ToDisplayText().Length > 0);
}
