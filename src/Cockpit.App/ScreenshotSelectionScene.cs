using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Screenshots;

namespace Cockpit.App;

// Headless harness scenes drive the surface's real input paths, because posing its view model would not prove a
// mode is reachable. That gap once let a surface that could not open pass 152 view tests (AC-357).
internal static class ScreenshotSelectionScene
{
    // The resting surface: the frozen desktop, nothing marked out, the hint above it.
    public const string Idle = "screenshot-selection";

    // A region dragged out — the marquee, the size readout, and everything outside it dimmed.
    public const string Region = "screenshot-selection-region";

    // A region with its eight grips showing (AC-565), dragged well clear of the window's own edges so none of
    // them sit off the visible surface. The grips already draw on `Region`'s own selection — this
    // scene exists so a reviewer does not have to know that to find them.
    public const string Grips = "screenshot-selection-grips";

    // Window mode, with the window under the pointer marked out.
    public const string WindowPick = "screenshot-selection-window";

    // Redaction mode, with boxes drawn over part of a region that was already chosen.
    public const string Redaction = "screenshot-selection-redaction";

    // The mark layer with both its tools on one region (AC-359): a frame around something to look at, and a box
    // over something not to send. One scene rather than two, because what is worth looking at is that they sit
    // on the same picture and are drawn in the order they were placed.
    public const string Marks = "screenshot-selection-marks";

    // Arrows across the stand-in desktop (AC-360), drawn deliberately from its light half into its dark half and
    // back the other way. Legibility over both is the thing this tool can most easily get wrong and the thing no
    // assertion catches: a mark that vanishes into a terminal is still a mark by every measure a test can take.
    public const string Arrow = "screenshot-selection-arrow";

    // Washes over both halves of the stand-in desktop (AC-361) — one band across the light document, another
    // across the dark terminal. The two are the tool's whole problem: ink over paper and ink over a terminal have
    // to move the pixels in opposite directions, and a scene with only one of them would show a tool that works.
    public const string Highlight = "screenshot-selection-highlight";

    // A freehand line drawn round something and another scribbled across the dark half (AC-362). Drawn as an arc
    // of many small steps, because the thing worth looking at is whether it comes out a curve or a polygon.
    public const string Stroke = "screenshot-selection-stroke";

    // Notes typed onto the capture (AC-363), one on each half of the stand-in desktop. Typed through the window's
    // own text input, so what the scene shows is what the keys actually do — including that they stopped being
    // shortcuts while the note was open.
    public const string Text = "screenshot-selection-text";

    // Two screens side by side, with the pointer left on the right-hand one. The surface is a single window
    // spanning every display, so its own middle is a place nobody is looking — this is the scene that shows
    // whether the control panel found the screen the operator is actually on (AC-358).
    public const string TwoDisplays = "screenshot-selection-two-displays";

    // Keep image pixels at 2x window units so coordinate conversion cannot pass accidentally at a 1:1 ratio, the
    // failure that made scaled-screen drags stop at two thirds (AC-329).
    private const int CaptureScale = 2;

    // This surface's scene names. One list rather than a name test written out a second time, so a mode that is
    // added here cannot be missing from whatever walks the scenes — the theme baseline (AC-338) reads it.
    public static IReadOnlyList<string> Names { get; } =
        [Idle, Region, Grips, WindowPick, Redaction, Marks, Arrow, Highlight, Stroke, Text, TwoDisplays];

    // Whether a scene name is one of this surface's, so the harness knows to build and stage it.
    public static bool Covers(string? scene) => scene is not null && Names.Contains(scene);

    // The surface over a stand-in desktop, sized to the run's own window size. Every mode builds the same
    // window — what tells them apart happens afterwards, in `Stage`, once it is on screen.
    public static ScreenshotSelectionWindow Build(string? scene, int width, int height)
    {
        var desktop = new CaptureRect(0, 0, width, height);
        var image = StandInDesktop.Draw(width * CaptureScale, height * CaptureScale);
        var window = ScreenshotSelectionWindow.Build(
            _Capture(desktop, image.PixelSize, scene == TwoDisplays), image, lastRegion: null, new StandInWindows(desktop));

        // The real surface takes its size from the screens it covers, and a headless run has none.
        window.Width = width;
        window.Height = height;

        return window;
    }

    // Puts a shown surface into the mode its scene name asks for, through the pointer and the keys — the same
    // route an operator takes. Called after the window is shown because that is when it has a size, and every
    // position here is measured against it.
    public static void Stage(ScreenshotSelectionWindow surface, string? scene)
    {
        var width = surface.ClientSize.Width;
        var height = surface.ClientSize.Height;

        switch (scene)
        {
            case Region:
                _Drag(surface, new Point(width * 0.22, height * 0.26), new Point(width * 0.70, height * 0.74));
                break;

            case Grips:
                // Clear of every edge on all four sides, so the corner and side grips this scene exists to show
                // are not clipped by the window's own border.
                _Drag(surface, new Point(width * 0.30, height * 0.30), new Point(width * 0.65, height * 0.65));
                break;

            case WindowPick:
                surface.KeyPressQwerty(PhysicalKey.W, RawInputModifiers.None);
                surface.MouseMove(new Point(width * 0.24, height * 0.40));
                break;

            case TwoDisplays:
                // Only the pointer: where the panel lands is the whole point, and a region would move it for a
                // different reason and muddle the two.
                surface.MouseMove(new Point(width * 0.78, height * 0.55));
                break;

            case Marks:
                // The region first, then a frame, then a box — in that order, because the order is the thing:
                // this is one list and one undo, and the picture has to show them sitting on the same surface.
                _Drag(surface, new Point(width * 0.14, height * 0.20), new Point(width * 0.86, height * 0.88));
                surface.KeyPressQwerty(PhysicalKey.O, RawInputModifiers.None);
                _Drag(surface, new Point(width * 0.18, height * 0.28), new Point(width * 0.47, height * 0.52));
                surface.KeyPressQwerty(PhysicalKey.B, RawInputModifiers.None);
                _Drag(surface, new Point(width * 0.55, height * 0.62), new Point(width * 0.81, height * 0.68));
                break;

            case Arrow:
                // Two of them, crossing between the light document and the dark editor, and pointing opposite
                // ways. One arrow proves neither thing this scene exists for: a single direction cannot show that
                // the head turns with the drag, and a single background cannot show that the mark survives both.
                _Drag(surface, new Point(width * 0.10, height * 0.10), new Point(width * 0.94, height * 0.90));
                surface.KeyPressQwerty(PhysicalKey.P, RawInputModifiers.None);
                _Drag(surface, new Point(width * 0.80, height * 0.20), new Point(width * 0.28, height * 0.60));
                _Drag(surface, new Point(width * 0.60, height * 0.80), new Point(width * 0.88, height * 0.44));
                break;

            case Text:
                // One note on the light half and one on the dark, with a word in each that is also a shortcut —
                // "Window" begins with the key that picks a window, and typing it must not.
                _Drag(surface, new Point(width * 0.10, height * 0.10), new Point(width * 0.94, height * 0.90));
                surface.KeyPressQwerty(PhysicalKey.T, RawInputModifiers.None);
                // The first note is placed off the panel rather than at a fraction. At 0.32 it pressed inside the
                // panel, so the press belonged to the panel and no note opened — after which the string ran as
                // shortcuts and Enter took the shot and closed the surface out from under the second note.
                _Note(surface, new Point(width * 0.58, _ClearOfTheControls(surface, height * 0.42)), "Window is empty here");
                _Note(surface, new Point(width * 0.58, height * 0.70), "expected 12, got 7");
                break;

            case Stroke:
                // A ring round a paragraph of the light document, and a line struck through the dark terminal. The
                // ring is what shows whether the curve survived: a circle made of straight segments is a polygon,
                // and at this many samples that is exactly what a chain of lines would look like.
                _Drag(surface, new Point(width * 0.10, height * 0.10), new Point(width * 0.94, height * 0.90));
                surface.KeyPressQwerty(PhysicalKey.D, RawInputModifiers.None);
                _Circle(surface, new Point(width * 0.74, height * 0.30), width * 0.10, height * 0.10);
                _Circle(surface, new Point(width * 0.74, height * 0.70), width * 0.13, height * 0.02);
                break;

            case Highlight:
                // A band over a line of the light document and another over a line of the dark terminal. Both, and
                // over text rather than over empty panel, because what has to be looked at is whether the words
                // under the wash survived it.
                _Drag(surface, new Point(width * 0.10, height * 0.10), new Point(width * 0.94, height * 0.90));
                surface.KeyPressQwerty(PhysicalKey.H, RawInputModifiers.None);
                // Measure below the fixed-size panel rather than using a window fraction that clears it only at
                // 1440x900; otherwise the document band silently disappears at the 1100x760 render size.
                var band = _ClearOfTheControls(surface, height * 0.42);
                _Drag(surface, new Point(width * 0.56, band), new Point(width * 0.90, band + (height * 0.045)));
                _Drag(surface, new Point(width * 0.56, height * 0.625), new Point(width * 0.90, height * 0.675));
                break;

            case Redaction:
                // Redaction requires a region first; run it under the panel to prove both the mode and that the
                // panel stays put rather than stepping aside.
                _Drag(surface, new Point(width * 0.14, height * 0.06), new Point(width * 0.86, height * 0.86));
                surface.KeyPressQwerty(PhysicalKey.B, RawInputModifiers.None);
                _Drag(surface, new Point(width * 0.20, height * 0.30), new Point(width * 0.44, height * 0.35));
                _Drag(surface, new Point(width * 0.58, height * 0.62), new Point(width * 0.80, height * 0.67));
                break;
        }

        _AssertStaged(surface, scene);
    }

    // Assert staged mark counts because pointer presses that hit the fixed-size panel are silently lost. This
    // caught note/highlighter scenes that passed at the 1440x900 test size but rendered short at 1100x760.
    private static readonly Dictionary<string, int> StagedMarks = new(StringComparer.Ordinal)
    {
        [Marks] = 2,
        [Arrow] = 2,
        [Highlight] = 2,
        [Stroke] = 2,
        [Redaction] = 2,
        [Text] = 2,
    };

    private static void _AssertStaged(ScreenshotSelectionWindow surface, string? scene)
    {
        if (scene is null || !StagedMarks.TryGetValue(scene, out var expected))
        {
            return;
        }

        var selection = surface.DataContext as ScreenshotSelectionViewModel
            ?? throw new InvalidOperationException($"The '{scene}' surface has no selection to have staged onto.");

        if (selection.Marks.Count != expected)
        {
            throw new InvalidOperationException(
                $"Scene '{scene}' staged {selection.Marks.Count} of its {expected} marks. A press that lands on the "
                + "control panel belongs to the panel, so a mark begun under it is never drawn — check the positions "
                + "against where the panels rest now.");
        }
    }

    // Derive y from the fixed-size panel, because a window fraction can clear it at one surface size and hit it at
    // another, silently dropping staged marks.
    private static double _ClearOfTheControls(ScreenshotSelectionWindow surface, double preferred)
    {
        // The panels are placed once the surface has a region to place them against, and their bounds are whatever
        // the last layout pass left — which, straight after a drag, is not yet this one.
        surface.UpdateLayout();

        // Both of them: the mark tools sit in a second panel stacked under the first, and measuring only the top
        // one left the note scene still pressing into a panel below 840x630. Whichever reaches lowest is the one
        // a press has to clear, and taking the lowest keeps that true if either panel grows.
        var lowest = surface.GetVisualDescendants()
            .OfType<Border>()
            .Where(border => border.Name is "Controls" or "MarkControls")
            .Select(border => border.TranslatePoint(new Point(0, border.Bounds.Height), surface)?.Y)
            .Where(bottom => bottom is not null)
            .DefaultIfEmpty(null)
            .Max();

        // A margin, because a press exactly on the seam belongs to whichever of the two the hit test reaches
        // first, and that is not something a scene should be deciding by a pixel.
        return lowest is null ? preferred : Math.Max(preferred, lowest.Value + 12);
    }

    // A note clicked open at a spot and typed into, through the window's own text input — which is the route an
    // operator's keyboard takes, and the one that would run the shortcuts instead if the surface let it.
    private static void _Note(ScreenshotSelectionWindow surface, Point at, string text)
    {
        surface.MouseDown(at, MouseButton.Left);
        surface.MouseUp(at, MouseButton.Left);
        surface.KeyTextInput(text);
        surface.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
    }

    // A ring drawn round a point, as a hand would draw it: a great many small moves rather than a handful of long
    // ones. Far enough apart that none of them are thinned away — thinning is for a hand that hesitates or shakes,
    // and a scene that fired it would be showing the exception rather than the ordinary case.
    private static void _Circle(ScreenshotSelectionWindow surface, Point centre, double acrossX, double acrossY)
    {
        const int steps = 48;

        var start = new Point(centre.X + acrossX, centre.Y);
        surface.MouseDown(start, MouseButton.Left);

        for (var step = 1; step <= steps; step++)
        {
            var angle = 2 * Math.PI * step / steps;
            surface.MouseMove(
                new Point(centre.X + (acrossX * Math.Cos(angle)), centre.Y + (acrossY * Math.Sin(angle))),
                RawInputModifiers.LeftMouseButton);
        }

        surface.MouseUp(start, MouseButton.Left);
    }

    private static void _Drag(ScreenshotSelectionWindow surface, Point from, Point to)
    {
        surface.MouseDown(from, MouseButton.Left);

        // Carrying the button, because the surface only treats a move as a drag while it is down — a move
        // without it is the pointer wandering, which is a different gesture entirely.
        surface.MouseMove(to, RawInputModifiers.LeftMouseButton);
        surface.MouseUp(to, MouseButton.Left);
    }

    // Use one display for appearance scenes and two for control-panel placement, whose purpose is to follow the
    // operator's screen rather than center across the combined window.
    private static ScreenCapture _Capture(CaptureRect desktop, PixelSize image, bool split) => new()
    {
        // The bytes are never decoded: the surface is handed the bitmap directly, and this only has to say where
        // the pixels came from. A capture off a real desktop carries the encoded image for what happens after.
        Image = [],
        Displays = split ? _SideBySide(desktop, image) : [_Display(desktop, new CaptureRect(0, 0, image.Width, image.Height))],
    };

    // Two screens meeting in the middle. The right-hand one takes what is left rather than half again, so an odd
    // width leaves neither a gap nor an overlap between them — a column belonging to no display is a column the
    // surface refuses to drag on.
    private static IReadOnlyList<CapturedDisplay> _SideBySide(CaptureRect desktop, PixelSize image)
    {
        var desktopSplit = desktop.Width / 2;
        var imageSplit = image.Width / 2;

        return
        [
            _Display(desktop with { Width = desktopSplit }, new CaptureRect(0, 0, imageSplit, image.Height)),
            _Display(
                new CaptureRect(desktopSplit, 0, desktop.Width - desktopSplit, desktop.Height),
                new CaptureRect(imageSplit, 0, image.Width - imageSplit, image.Height)),
        ];
    }

    private static CapturedDisplay _Display(CaptureRect desktop, CaptureRect image) => new()
    {
        DesktopBounds = desktop,
        Scale = CaptureScale,
        ImageBounds = image,
    };

    // Windows for the picker to highlight, laid out on the stand-in desktop where the panels were drawn, so the
    // rectangle that lights up is around something rather than around nothing.
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
