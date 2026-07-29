using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.Core.Abstractions.Screenshots;
using Cockpit.Infrastructure.Screenshots;

namespace Cockpit.Infrastructure.Tests.Screenshots;

/// <summary>
/// The Windows capture against a faked screen (AC-327). What GDI does needs a desktop and is verified by hand;
/// what is made of what it returns — which monitor's pixels sit where in the image — is arithmetic, and it is
/// the arithmetic every crop the selection UI makes runs through.
/// </summary>
public class WindowsScreenshotCaptureTests
{
    /// <summary>The ordinary machine: one monitor, and the image is it.</summary>
    [Fact]
    public async Task OneMonitor_IsTheWholeImage()
    {
        var capture = _Capture(new StubWindowsScreen
        {
            VirtualBounds = new CaptureRect(0, 0, 1920, 1080),
            Displays = [_Display(0, 0, 1920, 1080)],
        });

        var result = await capture.CaptureAsync();

        Assert.NotNull(result);
        Assert.Single(result!.Displays);
        Assert.Equal(new CaptureRect(0, 0, 1920, 1080), result.Displays[0].ImageBounds);
    }

    /// <summary>
    /// Two monitors at different scales — the arrangement the whole epic is careful about. On Windows a
    /// per-monitor-aware process is given both the metrics and the rectangles in real pixels, so a display's
    /// width on the desktop is its width in the image and nothing is multiplied by anything.
    /// </summary>
    [Fact]
    public async Task TwoMonitorsAtDifferentScales_MapToTheirOwnPixels()
    {
        var capture = _Capture(new StubWindowsScreen
        {
            VirtualBounds = new CaptureRect(0, 0, 4800, 1620),
            Displays = [_Display(0, 0, 2880, 1620, scale: 1.5), _Display(2880, 0, 1920, 1080)],
        });

        var result = await capture.CaptureAsync();

        Assert.Equal(new CaptureRect(0, 0, 2880, 1620), result!.Displays[0].ImageBounds);
        Assert.Equal(new CaptureRect(2880, 0, 1920, 1080), result.Displays[1].ImageBounds);
        Assert.Equal(1.5, result.Displays[0].Scale);
        Assert.Equal(new CapturePoint(2880, 0), result.ToImagePixel(new CapturePoint(2880, 0)));
    }

    /// <summary>
    /// A monitor placed to the left of the primary sits at a negative x, and the virtual screen starts there.
    /// The image starts at its own corner regardless, so every display shifts by that corner — red if the blit's
    /// origin is treated as (0,0), which puts the secondary monitor's pixels outside the image entirely.
    /// </summary>
    [Fact]
    public async Task AMonitorLeftOfThePrimary_ShiftsByTheVirtualScreensCorner()
    {
        var capture = _Capture(new StubWindowsScreen
        {
            VirtualBounds = new CaptureRect(-1920, 0, 3840, 1080),
            Displays = [_Display(-1920, 0, 1920, 1080), _Display(0, 0, 1920, 1080)],
        });

        var result = await capture.CaptureAsync();

        Assert.Equal(new CaptureRect(0, 0, 1920, 1080), result!.Displays[0].ImageBounds);
        Assert.Equal(new CaptureRect(1920, 0, 1920, 1080), result.Displays[1].ImageBounds);
        Assert.Equal(new CapturePoint(0, 0), result.ToImagePixel(new CapturePoint(-1920, 0)));
    }

    /// <summary>
    /// The blit is asked for the virtual screen, not for a display — an L-shaped arrangement leaves area no
    /// monitor covers, and the capture has to span it or the second monitor's rows are missing.
    /// </summary>
    [Fact]
    public async Task TheBlitIsAskedForTheWholeVirtualScreen()
    {
        var screen = new StubWindowsScreen
        {
            VirtualBounds = new CaptureRect(0, 0, 3840, 1440),
            Displays = [_Display(0, 360, 1920, 1080), _Display(1920, 0, 1920, 1440)],
        };

        await _Capture(screen).CaptureAsync();

        Assert.Equal(new CaptureRect(0, 0, 3840, 1440), screen.Requested);
    }

    /// <summary>A virtual screen with no area is a desktop nobody can capture, and blitting zero by zero would hand back an empty image as though it had worked.</summary>
    [Fact]
    public async Task AVirtualScreenWithNoArea_IsRefused()
    {
        var capture = _Capture(new StubWindowsScreen
        {
            VirtualBounds = new CaptureRect(0, 0, 0, 0),
            Displays = [],
        });

        var act = async () => await capture.CaptureAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(act);
        Assert.Contains("no area", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Cancelling is immediate. The old route waited two minutes on a clipboard that might never change; this
    /// one has nothing to wait for, so the only honest moment to give up is before the screen is read at all.
    /// </summary>
    [Fact]
    public async Task Cancelling_StopsBeforeTheScreenIsRead()
    {
        var screen = new StubWindowsScreen
        {
            VirtualBounds = new CaptureRect(0, 0, 1920, 1080),
            Displays = [_Display(0, 0, 1920, 1080)],
        };
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var act = async () => await _Capture(screen).CaptureAsync(cancellation.Token);

        await Assert.ThrowsAsync<OperationCanceledException>(act);
        Assert.Null(screen.Requested);
    }

    /// <summary>
    /// A display unplugged between reading the layout and reading the pixels. GDI does not complain — the blit
    /// clips to whatever is there now — so the image comes back the size that was asked for while describing a
    /// desktop that no longer exists. Cropping by that layout takes the wrong region and looks entirely normal.
    /// </summary>
    [Fact]
    public async Task ADisplayChangingMidCapture_IsRefused()
    {
        var capture = _Capture(new StubWindowsScreen
        {
            VirtualBounds = new CaptureRect(0, 0, 3840, 1080),
            Displays = [_Display(0, 0, 1920, 1080), _Display(1920, 0, 1920, 1080)],
            VirtualBoundsAfterCapture = new CaptureRect(0, 0, 1920, 1080),
        });

        var act = async () => await capture.CaptureAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(act);
        Assert.Contains("changed while", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A process that is not per-monitor aware still captures — one screen is self-consistent at any scale — so this is a warning, not a refusal.</summary>
    [Fact]
    public async Task AProcessThatIsNotPerMonitorAware_StillCaptures()
    {
        var capture = _Capture(new StubWindowsScreen
        {
            VirtualBounds = new CaptureRect(0, 0, 1920, 1080),
            Displays = [_Display(0, 0, 1920, 1080)],
            IsPerMonitorDpiAware = false,
        });

        Assert.NotNull((await capture.CaptureAsync()));
    }

    private static WindowsScreenshotCapture _Capture(IWindowsScreenReader screen) =>
        new(screen, NullLogger<WindowsScreenshotCapture>.Instance);

    private static DesktopDisplay _Display(int x, int y, int width, int height, double scale = 1.0) =>
        new() { Bounds = new CaptureRect(x, y, width, height), Scale = scale };
}
