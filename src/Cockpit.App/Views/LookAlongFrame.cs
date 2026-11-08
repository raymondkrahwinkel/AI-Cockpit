using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Core.Configuration;
using Material.Icons;
using Material.Icons.Avalonia;

namespace Cockpit.App.Views;

// AC-2209: the cyan frame round the display the assistant may look at. Clicks fall through it, and it stays out of
// what the assistant sees (left out of the capture on Windows, hidden just before it elsewhere).
internal sealed class LookAlongFrameWindow : Window
{
    public LookAlongFrameWindow()
    {
        Title = $"{CockpitProduct.DisplayName} look-along frame";
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        CanResize = false;
        Focusable = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        var frame = new Border { BorderThickness = new Thickness(3) };
        frame.Bind(Border.BorderBrushProperty, frame.GetResourceObservable("CockpitStatusBusyBrush"));
        Content = frame;
        Opened += (_, _) =>
        {
            OverlayWindows.MakeClickThrough(this);
            OverlayWindows.ExcludeFromCapture(this);
        };
    }

    public void Cover(PixelRect display)
    {
        var scale = Screens.All.FirstOrDefault(screen => screen.Bounds == display)?.Scaling ?? 1;
        Position = display.Position;
        Width = display.Width / scale;
        Height = display.Height / scale;
    }
}

// AC-2209: the tab on the frame, saying when the assistant last looked, with Stop. It is the one clickable part.
internal sealed class LookAlongTabWindow : Window
{
    private readonly TextBlock _lastLook = new() { FontSize = 12 };

    public LookAlongTabWindow()
    {
        Title = $"{CockpitProduct.DisplayName} look-along";
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Content = CreateCard(_lastLook, () => StopRequested?.Invoke(this, EventArgs.Empty));
        Opened += (_, _) => OverlayWindows.ExcludeFromCapture(this);
    }

    public event EventHandler? StopRequested;

    public void ShowLastLook(string text) => _lastLook.Text = text;

    // The card on its own, for this window and for the scene that renders it on a stand-in screen.
    internal static Border CreateCard(TextBlock lastLook, Action stop)
    {
        var icon = new MaterialIcon { Kind = MaterialIconKind.Eye, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        icon.Bind(MaterialIcon.ForegroundProperty, icon.GetResourceObservable("CockpitStatusBusyBrush"));
        var title = new TextBlock { Text = "The assistant may look at this screen", FontSize = 13, FontWeight = FontWeight.Medium };
        title.Bind(TextBlock.ForegroundProperty, title.GetResourceObservable("CockpitTextPrimaryBrush"));
        lastLook.Bind(TextBlock.ForegroundProperty, lastLook.GetResourceObservable("CockpitTextSecondaryBrush"));
        var button = new Button { Content = "Stop", Classes = { "Compact" }, VerticalAlignment = VerticalAlignment.Center };
        button.Click += (_, _) => stop();
        var card = new Border
        {
            Margin = new Thickness(CompanionPopOutCard.ShadowMargin),
            Padding = new Thickness(12, 8),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(12),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Children = { icon, new StackPanel { Spacing = 2, Children = { title, lastLook } }, button },
            },
        };
        card.Bind(Border.BackgroundProperty, card.GetResourceObservable("CockpitFloatingCardBrush"));
        card.Bind(Border.BorderBrushProperty, card.GetResourceObservable("CockpitStatusBusyBrush"));
        card.Bind(Border.BoxShadowProperty, card.GetResourceObservable("CockpitFloatingCardShadow"));

        return card;
    }

    // Top centre of the display, inside the frame; then the bottom and the corners, whichever covers neither the
    // strip nor an open pop-out. Pure geometry, so a wrong spot shows in the render.
    internal static PixelPoint Place(PixelRect display, PixelSize tab, IReadOnlyList<PixelRect> avoid)
    {
        const int inset = 8;
        var centre = display.X + ((display.Width - tab.Width) / 2);
        var top = display.Y + inset;
        var bottom = display.Bottom - inset - tab.Height;
        var left = display.X + inset;
        var right = display.Right - inset - tab.Width;
        PixelPoint[] spots = [new(centre, top), new(centre, bottom), new(left, top), new(right, top), new(left, bottom), new(right, bottom)];

        return spots.FirstOrDefault(spot => !avoid.Any(new PixelRect(spot, tab).Intersects), spots[0]);
    }
}
