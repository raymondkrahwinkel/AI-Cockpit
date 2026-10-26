using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Cockpit.App.Theming;
using Cockpit.Plugins.Abstractions;
using Material.Icons;

namespace Cockpit.App.Controls;

// AC-1013/AC-1763: A plugin's left-menu launcher (#14) as the mockups' `.nav` row — icon 16 + label, no border, hover
// tint — inside its plugin's SidebarSection. `badge` (AC-516) reuses the plugin store's count-pill look, subscribing on
// attach/unsubscribing on detach to avoid duplicate handlers.
internal sealed class PluginLauncherButton : Button
{
    // Avalonia's "Button" selector matches the type exactly, so without this a derived button is styled by nothing at
    // all: the cockpit's theme skips it, and the row loses the surface and border every other button in the sidebar
    // has. It is a button, and it should be styled as one.
    protected override Type StyleKeyOverride => typeof(Button);

    private readonly SideMenuButtonBadge? _badge;
    private readonly Border? _badgePill;
    private readonly TextBlock? _badgeText;

    public PluginLauncherButton(
        string title,
        Action onInvoke,
        SideMenuButtonBadge? badge = null,
        MaterialIconKind? iconKind = null)
    {
        Classes.Add("Subtle");
        HorizontalAlignment = HorizontalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Padding = new Thickness(10, 6);
        CornerRadius = new CornerRadius(0);
        this.Bind(ForegroundProperty, this.GetResourceObservable("CockpitTextSecondaryBrush"));
        Click += (_, _) => onInvoke();

        var content = new DockPanel();

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

        var label = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // A row without an icon of its own gets the plugin puzzle piece, so the labels of a card line up.
        label.Children.Add(CockpitIcons.Icon(iconKind ?? MaterialIconKind.PuzzleOutline, 16));
        label.Children.Add(new TextBlock { Text = title, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(label);

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
        (_badgePill.Background, _badgeText.Foreground) = _badge.Tone switch
        {
            SideMenuButtonBadgeTone.Success =>
                (ThemeBrush.Resolve("CockpitStatusDoneChipTintBrush", "#145AA576"), ThemeBrush.Resolve("CockpitStatusDoneBrush", "#5AA576")),
            SideMenuButtonBadgeTone.Neutral =>
                (ThemeBrush.Resolve("CockpitInsetBgBrush", "#16181d"), ThemeBrush.Resolve("CockpitTextSecondaryBrush", "#9aa3b2")),
            _ =>
                (ThemeBrush.Resolve("CockpitAccentBrush", "#2563eb"), ThemeBrush.Resolve("CockpitTextOnAccentBrush", "#ffffff")),
        };
        _badgePill.IsVisible = text.Length > 0;
    }

    private static CornerRadius _Radius(string key, double fallback) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is CornerRadius radius
            ? radius
            : new CornerRadius(fallback);
}
