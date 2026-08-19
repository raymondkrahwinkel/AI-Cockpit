using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Cockpit.App.Theming;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.Controls;

// AC-937: the sidebar's single collapsed-plugins launcher — shown only while at least one plugin's contribution is
// not pinned top-level, opening a right-hand flyout over the same PluginLauncherButton/PluginSectionControl
// instances the sidebar would otherwise have drawn directly. A plain Flyout, not a MenuFlyout: a MenuFlyout cannot
// host those controls' own gear and badge, the same reason CockpitView.axaml's workspace "+" uses one.
//
// The accent dot follows the same attach/detach subscribe pattern as PluginLauncherButton's own badge, just summed
// across every collapsed badge (AC-516) rather than showing one: the badges are not the same unit — 19 PRs plus 3
// issues is 22 of nothing — so this renders "something changed" rather than a number.
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

        var flyoutPanel = new StackPanel { Spacing = 4 };
        foreach (var control in collapsedControls)
        {
            flyoutPanel.Children.Add(control);
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
