using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Cockpit.App.Views;

// The shell every Companion pop-out sits in; where it goes is CompanionPopOutPlacer's call.
public partial class CompanionPopOut : Window
{
    public CompanionPopOut()
    {
        InitializeComponent();
        Opened += (_, _) => OverlayWindows.ExcludeFromCapture(this);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        };
    }

    public string PopOutKey { get; init; } = string.Empty;

    public Control Body
    {
        get => Card.Body!;
        init => Card.Body = value;
    }

    // Where the card was last placed, shadow margin excluded.
    internal PixelRect CardBounds { get; private set; }

    private int _ShadowPixels => (int)Math.Ceiling(CompanionPopOutCard.ShadowMargin * RenderScaling);

    // The size the content wants at this width, in pixels, for the placer to fit.
    internal PixelSize DesiredCardSize(double width)
    {
        var content = Body;
        content.Measure(new Size(width - 2, double.PositiveInfinity));

        return new PixelSize((int)Math.Ceiling(width * RenderScaling), (int)Math.Ceiling((content.DesiredSize.Height + 2) * RenderScaling));
    }

    internal void PlaceAt(PixelRect card)
    {
        CardBounds = card;
        var shadow = _ShadowPixels;
        Position = new PixelPoint(card.X - shadow, card.Y - shadow);
        Width = card.Width / RenderScaling + 2 * CompanionPopOutCard.ShadowMargin;
        Height = card.Height / RenderScaling + 2 * CompanionPopOutCard.ShadowMargin;
    }
}
