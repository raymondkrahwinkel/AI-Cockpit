using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Cockpit.Plugins.Abstractions.Files;

namespace Cockpit.App.Files;

// Who is in the file, in the header strip: a stack of avatars, and on a click the list with name, role, activity and source.
internal sealed class LivePresenceBar : ContentControl
{
    private readonly StackPanel _avatars = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly Popup _popup;
    private readonly StackPanel _list = new();
    private readonly TextBlock _saved = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _open = new() { FontSize = 11, Cursor = new Cursor(StandardCursorType.Hand), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _title = new() { FontSize = 11, FontWeight = FontWeight.SemiBold, Margin = new Thickness(14, 0, 14, 6) };
    private IReadOnlyList<LivePeer> _peers = [];

    public LivePresenceBar()
    {
        IsVisible = false;
        Padding = new Thickness(6, 0, 4, 0);
        Cursor = new Cursor(StandardCursorType.Hand);
        _title.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("CockpitTextSecondaryBrush"));
        _saved.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("CockpitTextFaintBrush"));
        _open.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("CockpitClickablePathFgBrush"));

        var footer = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(_open, Dock.Right);
        footer.Children.Add(_open);
        footer.Children.Add(_saved);
        var rule = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(14, 6, 14, 0), Padding = new Thickness(0, 8, 0, 0), Child = footer };
        rule.Bind(Border.BorderBrushProperty, this.GetResourceObservable("CockpitHairlineSoftBrush"));
        var panel = new Border { Width = 330, Padding = new Thickness(0, 10, 0, 8), BorderThickness = new Thickness(1), Child = new StackPanel { Children = { _title, _list, rule } } };
        panel.Bind(Border.BackgroundProperty, this.GetResourceObservable("CockpitPanelBgBrush"));
        panel.Bind(Border.BorderBrushProperty, this.GetResourceObservable("CockpitHairlineBrush"));
        panel.Bind(Border.CornerRadiusProperty, this.GetResourceObservable("CockpitPanelRadius"));
        var dark = ActualThemeVariant != ThemeVariant.Light;
        panel.BoxShadow = new BoxShadows(new BoxShadow { OffsetY = 10, Blur = 30, Color = Color.FromArgb(dark ? (byte)0x80 : (byte)0x29, 0, 0, 0) });
        _popup = new Popup
        {
            PlacementTarget = this,
            Placement = PlacementMode.BottomEdgeAlignedRight,
            HorizontalOffset = 24,
            IsLightDismissEnabled = true,
            Child = new Border { Padding = new Thickness(24, 6, 24, 36), Background = Brushes.Transparent, Child = panel },
        };
        Content = new Panel { Children = { _avatars, _popup } };
        _open.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            _popup.IsOpen = false;
            OpenRequested?.Invoke(this, EventArgs.Empty);
        };
        PointerPressed += (_, e) =>
        {
            if (_peers.Count > 0)
            {
                _popup.IsOpen = !_popup.IsOpen;
                e.Handled = true;
            }
        };
    }

    public event EventHandler? OpenRequested;

    public bool IsOpen
    {
        get => _popup.IsOpen;
        set => _popup.IsOpen = value;
    }

    // "Saved by Sanne · 14:12", and what the link at the foot is called ("Open in Depot ↗"); null hides either.
    public void SetFooter(string? saved, string? openLabel)
    {
        _saved.Text = saved;
        _open.Text = openLabel;
        _open.IsVisible = openLabel is not null;
    }

    public void SetPeers(IReadOnlyList<LivePeer> peers)
    {
        _peers = [.. peers.OrderByDescending(peer => peer.You)];
        IsVisible = _peers.Count > 0;
        ToolTip.SetTip(this, $"{_peers.Count} in this note");
        _avatars.Children.Clear();
        _list.Children.Clear();
        foreach (var peer in _peers)
        {
            var avatar = Avatar(peer, "CockpitChromeBgBrush");
            avatar.Margin = new Thickness(_avatars.Children.Count == 0 ? 0 : -4, 0, 0, 0);
            _avatars.Children.Add(avatar);
            _list.Children.Add(_Row(peer));
        }
        _title.Text = $"IN THIS NOTE · {_peers.Count}";
        if (_peers.Count == 0)
        {
            _popup.IsOpen = false;
        }
    }

    // 22 px: round, or a rounded square for an agent; initials in the person's colour on the inset surface, a 1.5 px ring in that
    // colour and a 1.5 px ring of the strip's own colour around it, so the overlapping avatars stay apart. Away fades to 35%.
    internal Control Avatar(LivePeer peer, string surroundKey)
    {
        var agent = peer.Source.Equals("agent", StringComparison.OrdinalIgnoreCase);
        var color = Color.TryParse(peer.Color, out var parsed) ? new SolidColorBrush(parsed) : null;
        Border Ring(double size, double radius, IBrush? fill, Control? child = null) => new()
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(radius), Background = fill, Child = child,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var initial = new TextBlock
        {
            Text = peer.Name.Trim().Length == 0 ? (peer.You ? "Y" : "?") : peer.Name.Trim()[..1].ToUpperInvariant(),
            FontSize = 9.5, FontWeight = FontWeight.Bold, Foreground = color,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var face = Ring(22, agent ? 6 : 11, null, initial);
        face.Bind(Border.BackgroundProperty, this.GetResourceObservable("CockpitInsetBgBrush"));
        var ring = Ring(25, agent ? 7.5 : 12.5, color, face);
        if (color is null)
        {
            // No colour from the source: a neutral ring and initial, never one made up here.
            ring.Bind(Border.BackgroundProperty, this.GetResourceObservable("CockpitTextFaintBrush"));
            initial.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("CockpitTextFaintBrush"));
        }

        var outer = Ring(28, agent ? 9 : 14, null, ring);
        outer.Bind(Border.BackgroundProperty, this.GetResourceObservable(surroundKey));
        return new Grid { Width = 22, Height = 22, Opacity = peer.Away ? 0.35 : 1, Children = { outer } };
    }

    private Control _Row(LivePeer peer)
    {
        var name = new TextBlock { Text = !peer.You ? peer.Name : peer.Name.Length == 0 ? "You" : $"{peer.Name} (you)", FontSize = 12.5, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        name.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("CockpitTextPrimaryBrush"));
        var detail = new TextBlock { Text = $"{peer.Role} · {peer.Activity}", FontSize = 11 };
        detail.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("CockpitTextFaintBrush"));
        var source = new TextBlock { Text = peer.Source, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        source.Bind(TextBlock.FontFamilyProperty, this.GetResourceObservable("CockpitMonoFont"));
        source.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("CockpitTextSecondaryBrush"));
        var pill = new Border { Padding = new Thickness(7, 1), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center, Child = source };
        pill.Bind(Border.BorderBrushProperty, this.GetResourceObservable("CockpitHairlineBrush"));
        pill.Bind(Border.CornerRadiusProperty, this.GetResourceObservable("CockpitPillRadius"));
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { name, detail } };
        DockPanel.SetDock(pill, Dock.Right);
        var avatar = Avatar(peer, "CockpitPanelBgBrush");
        avatar.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(avatar, Dock.Left);
        return new DockPanel { LastChildFill = true, Margin = new Thickness(14, 6), Children = { avatar, pill, text } };
    }
}
