using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Screenshots;

namespace Cockpit.App.ViewTests;

/// <summary>
/// Picking a whole window off the capture (AC-330). The capture already holds every pixel on the desktop, so
/// taking a window is a crop to its rectangle — which makes this arithmetic, and testable, on a machine whose
/// own window list has nothing to do with it.
/// </summary>
public class ScreenshotWindowPickingTests
{
    /// <summary>Nothing here draws a frame, so the colour only has to be a value the surface can carry.</summary>
    private const uint Accent = 0xFF3B82F6;

    /// <summary>A 150% panel: the desktop is 1920×1080, the capture 2880×1620. Window bounds arrive in the first space and have to be used in the second.</summary>
    private static readonly CapturedDisplay Panel = new()
    {
        DesktopBounds = new CaptureRect(0, 0, 1920, 1080),
        Scale = 1.5,
        ImageBounds = new CaptureRect(0, 0, 2880, 1620),
    };

    [Fact]
    public void AWindowUnderThePointer_IsMarkedOutInImagePixels()
    {
        var selection = _Surface(_Window("Editor", 100, 50, 800, 600));
        selection.PickWindows(true);

        selection.HoverAt(surfaceX: 200, surfaceY: 150);

        Assert.Equal("Editor", selection.HoveredWindow!.Title);
        Assert.Equal(new CaptureRect(150, 75, 1200, 900), selection.Selection);
    }

    /// <summary>Overlapping windows: the list is front to back, so the one on top is the one that gets picked.</summary>
    [Fact]
    public void WhereTwoWindowsOverlap_TheFrontOneIsPicked()
    {
        var selection = _Surface(
            _Window("Front", 100, 100, 400, 400),
            _Window("Behind", 0, 0, 1000, 1000));
        selection.PickWindows(true);

        selection.HoverAt(surfaceX: 200, surfaceY: 200);

        Assert.Equal("Front", selection.HoveredWindow!.Title);
    }

    [Fact]
    public void OverTheDesktopItself_NothingIsPicked()
    {
        var selection = _Surface(_Window("Editor", 100, 50, 200, 200));
        selection.PickWindows(true);

        selection.HoverAt(surfaceX: 1000, surfaceY: 900);

        Assert.Null(selection.HoveredWindow);
        Assert.Null(selection.Selection);
    }

    /// <summary>Until the mode is on, moving the pointer is a drag or nothing — the same pointer cannot mean both at once.</summary>
    [Fact]
    public void WithTheModeOff_HoveringChangesNothing()
    {
        var selection = _Surface(_Window("Editor", 100, 50, 800, 600));

        selection.HoverAt(surfaceX: 200, surfaceY: 150);

        Assert.Null(selection.HoveredWindow);
        Assert.Null(selection.Selection);
    }

    /// <summary>
    /// On a desktop that will not say where its windows are, the mode is not merely empty — it is unavailable,
    /// and the surface can say so. A button that silently does nothing is what AC-220 was rejected for.
    /// </summary>
    [Fact]
    public void OnADesktopThatWillNotSay_TheModeIsUnavailable()
    {
        var selection = new ScreenshotSelectionViewModel(_Capture(), 2880, 1620, Accent, null, StubWindows.None)
        {
            SurfaceWidth = 1920,
            SurfaceHeight = 1080,
        };

        Assert.False(selection.CanPickWindow);

        selection.PickWindows(true);

        Assert.False(selection.PickingWindow, "asking for a mode that does not exist must not turn it on");
        Assert.Contains("not something this desktop will allow", selection.Hint);
    }

    /// <summary>
    /// Where it does exist, the mode is on offer — an operator cannot use one nobody mentioned. What does the
    /// mentioning is the panel's Window tool, which carries the key beside its name; that it is offered rather
    /// than greyed out is <see cref="ScreenshotControlPanelTests.AnAvailableTool_IsOfferedRatherThanGreyedOut"/>.
    /// </summary>
    [Fact]
    public void OnADesktopThatCanPickWindows_TheModeIsAvailable()
    {
        Assert.True(_Surface(_Window("Editor", 0, 0, 100, 100)).CanPickWindow);
    }

    /// <summary>A window off the edge of every display — minimised, or on a screen this capture does not cover — has no pixels here to crop.</summary>
    [Fact]
    public void AWindowWithNoPixelsInTheCapture_IsNotOffered()
    {
        var selection = _Surface(_Window("Elsewhere", 5000, 5000, 400, 300));
        selection.PickWindows(true);

        selection.HoverAt(surfaceX: 100, surfaceY: 100);

        Assert.Null(selection.HoveredWindow);
    }

    /// <summary>
    /// A window hanging over the edge of the captured desktop is still plainly visible, and the part that was
    /// captured is still croppable. Mapping only its two corners drops it entirely — so what is offered is the
    /// part the capture actually holds.
    /// </summary>
    [Fact]
    public void AWindowHalfOffTheScreen_IsOfferedForThePartThatWasCaptured()
    {
        var selection = _Surface(_Window("Half out", 1700, 100, 600, 400));
        selection.PickWindows(true);

        selection.HoverAt(surfaceX: 1800, surfaceY: 200);

        Assert.Equal("Half out", selection.HoveredWindow!.Title);
        Assert.Equal(new CaptureRect(2550, 150, 330, 600), selection.Selection);
    }

    /// <summary>Switching to everything leaves window mode behind, or the next pointer move would silently replace the selection.</summary>
    [Fact]
    public void TakingEverything_LeavesWindowMode()
    {
        var selection = _Surface(_Window("Editor", 100, 50, 800, 600));
        selection.PickWindows(true);

        selection.PickWindows(false);
        selection.SelectEverything();

        Assert.False(selection.PickingWindow);
        Assert.Null(selection.HoveredWindow);
        Assert.Equal(new CaptureRect(0, 0, 2880, 1620), selection.Selection);
    }

    private static ScreenshotSelectionViewModel _Surface(params DesktopWindow[] windows) =>
        new(_Capture(), 2880, 1620, Accent, null, new StubWindows { Windows = windows })
        {
            SurfaceWidth = 1920,
            SurfaceHeight = 1080,
        };

    private static DesktopWindow _Window(string title, int x, int y, int width, int height) =>
        new() { Title = title, Bounds = new CaptureRect(x, y, width, height) };

    private static ScreenCapture _Capture() =>
        new() { Image = [0x89, 0x50, 0x4E, 0x47], Displays = [Panel] };

    private sealed class StubWindows : IDesktopWindows
    {
        public static StubWindows None => new() { IsSupported = false };

        public bool IsSupported { get; init; } = true;

        public IReadOnlyList<DesktopWindow> Windows { get; init; } = [];

        public IReadOnlyList<DesktopWindow> Enumerate() => Windows;
    }
}
