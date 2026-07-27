using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Screenshots;

namespace Cockpit.App;

/// <summary>
/// The selection surface as scenes for the screenshot harness (AC-357), one per mode it can be in. Nothing here
/// changes what an operator sees; it exists so the surface can be looked at without a display and without anyone
/// present — which every other window in this app could already be, and this one could not.
/// </summary>
/// <remarks>
/// The modes are reached by driving the window's own input handling rather than by setting the view model,
/// because a mode nobody can get to is not a mode. That is the whole reason this scene is worth having: the
/// surface once shipped unable to open at all while 152 view tests stayed green, every one of them stopping at
/// the arithmetic. A render of a posed view model would have stayed green too.
/// </remarks>
internal static class ScreenshotSelectionScene
{
    /// <summary>The resting surface: the frozen desktop, nothing marked out, the hint above it.</summary>
    public const string Idle = "screenshot-selection";

    /// <summary>A region dragged out — the marquee, the size readout, and everything outside it dimmed.</summary>
    public const string Region = "screenshot-selection-region";

    /// <summary>Window mode, with the window under the pointer marked out.</summary>
    public const string WindowPick = "screenshot-selection-window";

    /// <summary>Redaction mode, with boxes drawn over part of a region that was already chosen.</summary>
    public const string Redaction = "screenshot-selection-redaction";

    /// <summary>
    /// How much bigger the stand-in capture is than the surface drawing it. Two, rather than one, so the ratio
    /// between window units and image pixels is not 1: a capture the same size as its window makes every
    /// conversion look right whether or not it is, and a surface that only worked at 1 is how AC-329 came to
    /// refuse every drag past two thirds of a scaled screen.
    /// </summary>
    private const int CaptureScale = 2;

    /// <summary>Whether a scene name is one of this surface's, so the harness knows to build and stage it.</summary>
    public static bool Covers(string? scene) => scene is Idle or Region or WindowPick or Redaction;

    /// <summary>
    /// The surface over a stand-in desktop, sized to the run's own window size. Every mode builds the same
    /// window — what tells them apart happens afterwards, in <see cref="Stage"/>, once it is on screen.
    /// </summary>
    public static ScreenshotSelectionWindow Build(int width, int height)
    {
        var desktop = new CaptureRect(0, 0, width, height);
        var image = _StandInDesktop(width * CaptureScale, height * CaptureScale);
        var window = ScreenshotSelectionWindow.Build(
            _Capture(desktop, image.PixelSize), image, lastRegion: null, new StandInWindows(desktop));

        // The real surface takes its size from the screens it covers, and a headless run has none.
        window.Width = width;
        window.Height = height;

        return window;
    }

    /// <summary>
    /// Puts a shown surface into the mode its scene name asks for, through the pointer and the keys — the same
    /// route an operator takes. Called after the window is shown because that is when it has a size, and every
    /// position here is measured against it.
    /// </summary>
    public static void Stage(ScreenshotSelectionWindow surface, string? scene)
    {
        var width = surface.ClientSize.Width;
        var height = surface.ClientSize.Height;

        switch (scene)
        {
            case Region:
                _Drag(surface, new Point(width * 0.22, height * 0.26), new Point(width * 0.70, height * 0.74));
                break;

            case WindowPick:
                surface.KeyPressQwerty(PhysicalKey.W, RawInputModifiers.None);
                surface.MouseMove(new Point(width * 0.24, height * 0.40));
                break;

            case Redaction:
                // A region first: redaction is refused until there is something to hide part of, so a scene that
                // skipped this would render the refusal rather than the mode.
                _Drag(surface, new Point(width * 0.14, height * 0.18), new Point(width * 0.86, height * 0.82));
                surface.KeyPressQwerty(PhysicalKey.B, RawInputModifiers.None);
                _Drag(surface, new Point(width * 0.20, height * 0.30), new Point(width * 0.44, height * 0.35));
                _Drag(surface, new Point(width * 0.58, height * 0.62), new Point(width * 0.80, height * 0.67));
                break;
        }
    }

    private static void _Drag(ScreenshotSelectionWindow surface, Point from, Point to)
    {
        surface.MouseDown(from, MouseButton.Left);

        // Carrying the button, because the surface only treats a move as a drag while it is down — a move
        // without it is the pointer wandering, which is a different gesture entirely.
        surface.MouseMove(to, RawInputModifiers.LeftMouseButton);
        surface.MouseUp(to, MouseButton.Left);
    }

    /// <summary>
    /// One display holding the whole stand-in image. Deliberately one: what a scene proves is how the surface
    /// looks, and a second display changes only which points refuse a drag — arithmetic
    /// <see cref="ViewModels.ScreenshotSelectionViewModel"/>'s own tests already hold, on layouts far nastier
    /// than anything worth drawing here.
    /// </summary>
    private static ScreenCapture _Capture(CaptureRect desktop, PixelSize image) => new()
    {
        // The bytes are never decoded: the surface is handed the bitmap directly, and this only has to say where
        // the pixels came from. A capture off a real desktop carries the encoded image for what happens after.
        Image = [],
        Displays =
        [
            new CapturedDisplay
            {
                DesktopBounds = desktop,
                Scale = CaptureScale,
                ImageBounds = new CaptureRect(0, 0, image.Width, image.Height),
            },
        ],
    };

    /// <summary>
    /// A desktop to stand in for the operator's. Drawn rather than filled: the surface dims what is outside the
    /// selection and strokes a line around what is inside, and both of those look fine over a flat colour no
    /// matter how wrong they are. What is needed is somewhere genuinely light and somewhere genuinely dark, with
    /// text-sized detail in each — which is also what tells you whether a redaction box actually covers anything.
    /// </summary>
    /// <remarks>
    /// None of these colours are the cockpit's own tokens, on purpose. This is somebody else's screen.
    /// </remarks>
    private static RenderTargetBitmap _StandInDesktop(int width, int height)
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(width, height));
        using var context = bitmap.CreateDrawingContext();

        context.FillRectangle(
            new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.Parse("#0f1420"), 0),
                    new GradientStop(Color.Parse("#243349"), 1),
                },
            },
            new Rect(0, 0, width, height));

        // An editor, dark, filling the left: the case a marquee stroke has to stay visible against.
        _Panel(context, _Area(width, height, 0.04, 0.08, 0.46, 0.74), "#161a22", "#232936");
        _Lines(context, _Area(width, height, 0.07, 0.18, 0.43, 0.70), "#7f8ea3", height * 0.014, [0.62, 0.88, 0.41, 0.75, 0.55, 0.93, 0.34, 0.68, 0.80, 0.47]);

        // A document, light, top right: the case the dim outside the selection has to be visible over.
        _Panel(context, _Area(width, height, 0.52, 0.08, 0.96, 0.50), "#fbfaf7", "#e6e3dd");
        _Lines(context, _Area(width, height, 0.55, 0.17, 0.93, 0.47), "#8d8880", height * 0.013, [0.90, 0.72, 0.85, 0.55, 0.78, 0.66]);

        // A terminal, near black, bottom right — the thing a redaction box most often has to cover.
        _Panel(context, _Area(width, height, 0.52, 0.54, 0.96, 0.86), "#080a0f", "#1a1f2b");
        _Lines(context, _Area(width, height, 0.55, 0.61, 0.93, 0.83), "#6ee7a8", height * 0.013, [0.48, 0.71, 0.36, 0.62, 0.29]);

        // A dock, and one bright tile on it, so the picture has a genuinely light spot outside the document too.
        context.FillRectangle(new SolidColorBrush(Color.Parse("#1c2331"), 0.85), _Area(width, height, 0.30, 0.90, 0.70, 0.97), (float)(height * 0.008));
        context.FillRectangle(new SolidColorBrush(Color.Parse("#f4c150")), _Area(width, height, 0.46, 0.915, 0.54, 0.955), (float)(height * 0.006));

        return bitmap;
    }

    private static void _Panel(DrawingContext context, Rect area, string body, string chrome)
    {
        context.FillRectangle(new SolidColorBrush(Color.Parse(body)), area);
        context.FillRectangle(new SolidColorBrush(Color.Parse(chrome)), area.WithHeight(area.Height * 0.09));
    }

    private static void _Lines(DrawingContext context, Rect area, string ink, double thickness, double[] widths)
    {
        var brush = new SolidColorBrush(Color.Parse(ink));
        var step = area.Height / widths.Length;

        for (var index = 0; index < widths.Length; index++)
        {
            context.FillRectangle(
                brush,
                new Rect(area.X, area.Y + (index * step), area.Width * widths[index], thickness),
                (float)(thickness / 2));
        }
    }

    private static Rect _Area(int width, int height, double left, double top, double right, double bottom) =>
        new(width * left, height * top, width * (right - left), height * (bottom - top));

    /// <summary>
    /// Windows for the picker to highlight, laid out on the stand-in desktop where the panels were drawn, so the
    /// rectangle that lights up is around something rather than around nothing.
    /// </summary>
    private sealed class StandInWindows(CaptureRect desktop) : IDesktopWindows
    {
        public bool IsSupported => true;

        public IReadOnlyList<DesktopWindow> Enumerate() =>
        [
            new DesktopWindow { Title = "notes.md — Editor", Bounds = _Rect(0.04, 0.08, 0.46, 0.74) },
            new DesktopWindow { Title = "Quarterly report.odt", Bounds = _Rect(0.52, 0.08, 0.96, 0.50) },
            new DesktopWindow { Title = "zsh — 92×24", Bounds = _Rect(0.52, 0.54, 0.96, 0.86) },
        ];

        private CaptureRect _Rect(double left, double top, double right, double bottom) =>
            new(
                (int)(desktop.Width * left),
                (int)(desktop.Height * top),
                (int)(desktop.Width * (right - left)),
                (int)(desktop.Height * (bottom - top)));
    }
}
