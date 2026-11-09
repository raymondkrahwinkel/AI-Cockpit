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

    // The card on its own, for this window and for the scene that renders it on a stand-in screen. AC-2190: the
    // companion-strook mockup's tab — one 30px line on the cyan of the frame, dark ink, Stop as the inverted button.
    internal static Border CreateCard(TextBlock lastLook, Action stop)
    {
        var icon = new MaterialIcon { Kind = MaterialIconKind.EyeOutline, Width = 15, Height = 15, VerticalAlignment = VerticalAlignment.Center };
        icon.Bind(MaterialIcon.ForegroundProperty, icon.GetResourceObservable("CockpitTextOnStatusBrush"));
        var title = new TextBlock { Text = "The assistant may look at this screen", FontSize = 12, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        title.Bind(TextBlock.ForegroundProperty, title.GetResourceObservable("CockpitTextOnStatusBrush"));
        lastLook.FontSize = 11;
        lastLook.FontWeight = FontWeight.Medium;
        lastLook.Opacity = 0.85;
        lastLook.VerticalAlignment = VerticalAlignment.Center;
        lastLook.Bind(TextBlock.ForegroundProperty, lastLook.GetResourceObservable("CockpitTextOnStatusBrush"));
        var label = new TextBlock { Text = "Stop", FontSize = 11.5, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        label.Bind(TextBlock.ForegroundProperty, label.GetResourceObservable("CockpitStatusBusyBrush"));
        var button = new Button
        {
            Content = label,
            Height = 22,
            Padding = new Thickness(8, 0),
            CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        button.Bind(Button.BackgroundProperty, button.GetResourceObservable("CockpitTextOnStatusBrush"));
        button.Click += (_, _) => stop();
        var card = new Border
        {
            Margin = new Thickness(CompanionPopOutCard.ShadowMargin),
            Height = 30,
            Padding = new Thickness(10, 0, 4, 0),
            CornerRadius = new CornerRadius(7),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { icon, title, lastLook, button },
            },
        };
        card.Bind(Border.BackgroundProperty, card.GetResourceObservable("CockpitStatusBusyBrush"));
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
