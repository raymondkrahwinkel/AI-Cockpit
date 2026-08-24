using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Cockpit.App.Theming;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.Controls;

// AC-1013: A plugin's left-menu launcher (#14) with an optional settings gear nested inside it — the
// gear's `Button.Click` must be stopped from bubbling to the launcher's own click. `badge` (AC-516) reuses
// the plugin store's count-pill look, subscribing on attach/unsubscribing on detach to avoid duplicate handlers.
internal sealed class PluginLauncherButton : Button
{
    // Avalonia's "Button" selector matches the type exactly, so without this a derived button is styled by nothing at
    // all: the cockpit's theme skips it, and the row loses the surface and border every other button in the sidebar
    // has. It is a button, and it should be styled as one.
    protected override Type StyleKeyOverride => typeof(Button);

    private readonly SideMenuButtonBadge? _badge;
    private readonly Border? _badgePill;
    private readonly TextBlock? _badgeText;

    public PluginLauncherButton(string title, Action onInvoke, Action? onSettings = null, SideMenuButtonBadge? badge = null)
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Click += (_, _) => onInvoke();

        var content = new DockPanel();

        if (onSettings is not null)
        {
            var gear = new Button
            {
                Content = CockpitIcons.Gear(),
                Classes = { "Subtle" },
                Padding = new Thickness(6, 2),
                Margin = new Thickness(6, 0, -6, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(gear, $"{title} settings");
            gear.Click += (_, e) =>
            {
                e.Handled = true;
                onSettings();
            };
            DockPanel.SetDock(gear, Dock.Right);
            content.Children.Add(gear);
        }

        if (badge is not null)
        {
            _badge = badge;
            _badgeText = new TextBlock
            {
                FontSize = 10,
                FontWeight = FontWeight.SemiBold,
                Foreground = ThemeBrush.Resolve("CockpitTextOnAccentBrush", "#ffffff"),
            };
            _badgePill = new Border
            {
                Background = ThemeBrush.Resolve("CockpitAccentBrush", "#2563eb"),
                CornerRadius = _Radius("CockpitPillRadius", 20),
                Padding = new Thickness(7, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
                Child = _badgeText,
            };
            _RenderBadge();
            DockPanel.SetDock(_badgePill, Dock.Right);
            content.Children.Add(_badgePill);
        }

        content.Children.Add(new TextBlock
        {
            Text = title,
            VerticalAlignment = VerticalAlignment.Center,
        });

        Content = content;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_badge is not null)
        {
            _badge.Changed += _OnBadgeChanged;
            // The badge may have changed between construction and this button actually landing in the tree (a
            // plugin rarely knows a count at Initialize time), so re-render once on attach rather than trusting
            // the constructor's snapshot to still be current.
            _RenderBadge();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_badge is not null)
        {
            _badge.Changed -= _OnBadgeChanged;
        }

        base.OnDetachedFromVisualTree(e);
    }

    // AC-516: a plugin typically updates its counter from a background fetch, never from the UI thread — so this
    // marshals itself rather than trusting the caller to.
    private void _OnBadgeChanged() => Dispatcher.UIThread.Post(_RenderBadge);

    // The one place this button decides what the badge shows — see SideMenuButtonBadge.ToDisplayText for the rule
    // (null Primary = nothing, Primary alone = that number including "0", both set = "primary / secondary").
    private void _RenderBadge()
    {
        if (_badge is null || _badgePill is null || _badgeText is null)
        {
            return;
        }

        var text = _badge.ToDisplayText();
        _badgeText.Text = text;
        _badgePill.IsVisible = text.Length > 0;
    }

    private static CornerRadius _Radius(string key, double fallback) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is CornerRadius radius
            ? radius
            : new CornerRadius(fallback);
}
