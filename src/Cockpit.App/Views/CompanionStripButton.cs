using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Cockpit.App.Controls;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.Views;

// A strip item's icon button: the tool's icon, its tooltip and, when the tool sets one, its badge in the corner.
internal sealed class CompanionStripButton : Button
{
    private readonly SideMenuButtonBadge? _badge;
    private readonly Border? _pill;
    private readonly TextBlock? _pillText;

    public CompanionStripButton(CompanionStripItem item)
    {
        Classes.Add("Subtle");
        Padding = new Thickness(7, 6);
        ToolTip.SetTip(this, item.Tooltip.Length > 0 ? item.Tooltip : item.Title);
        AutomationProperties.SetName(this, item.Title);

        var content = new Grid();
        content.Children.Add(item.IconKind is { } kind
            ? CockpitIcons.Icon(kind, 18)
            : new TextBlock { Text = item.Glyph.Length > 0 ? item.Glyph : item.Title[..1], FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center });
        if (item.Badge is { } badge)
        {
            _badge = badge;
            _pill = BadgePill.Create(out _pillText);
            _pill.HorizontalAlignment = HorizontalAlignment.Right;
            _pill.VerticalAlignment = VerticalAlignment.Top;
            _pill.Margin = new Thickness(0, -9, -12, 0);
            _pill.Padding = new Thickness(4, 0);
            content.Children.Add(_pill);
            _Render();
        }

        Content = content;
    }

    protected override Type StyleKeyOverride => typeof(Button);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_badge is not null)
        {
            _badge.Changed += _OnBadgeChanged;
            _Render();
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

    // A plugin sets its badge from wherever it counts, so this marshals itself.
    private void _OnBadgeChanged() => Dispatcher.UIThread.Post(_Render);

    private void _Render() => BadgePill.Render(_badge!, _pill!, _pillText!);
}
