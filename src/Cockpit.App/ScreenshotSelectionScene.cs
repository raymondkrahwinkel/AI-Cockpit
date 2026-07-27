using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
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
        var image = StandInDesktop.Draw(width * CaptureScale, height * CaptureScale);
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
