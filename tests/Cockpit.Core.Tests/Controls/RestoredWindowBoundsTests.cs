using Avalonia;
using Cockpit.App.Controls;
using Cockpit.Core.Layout;

namespace Cockpit.Core.Tests.Controls;

/// <summary>
/// AC-867: a saved window position is only worth restoring when a meaningful part of it overlaps a
/// currently-connected screen's <c>WorkingArea</c>, not just its raw <c>Bounds</c>.
/// </summary>
public class RestoredWindowBoundsTests
{
    private static readonly PixelRect PrimaryWorkingArea = new(0, 0, 1920, 1040);

    [Fact]
    public void IsOnAScreen_WhenFullyOnScreen_IsAccepted()
    {
        var bounds = new WindowBounds(100, 100, 800, 600, IsMaximized: false);

        Assert.True(RestoredWindowBounds.IsOnAScreen(bounds, [PrimaryWorkingArea]));
    }

    [Fact]
    public void IsOnAScreen_WhenFullyOffAllScreens_IsRejected()
    {
        var bounds = new WindowBounds(5000, 5000, 800, 600, IsMaximized: false);

        Assert.False(RestoredWindowBounds.IsOnAScreen(bounds, [PrimaryWorkingArea]));
    }

    [Fact]
    public void IsOnAScreen_WithOnlyAOnePixelOverlap_IsRejected()
    {
        // Left edge of the window sits 1px inside the screen's right edge — the old 1px-overlap check accepted
        // this even though nothing of the window is actually reachable.
        var bounds = new WindowBounds(PrimaryWorkingArea.Width - 1, 100, 800, 600, IsMaximized: false);

        Assert.False(RestoredWindowBounds.IsOnAScreen(bounds, [PrimaryWorkingArea]));
    }

    [Fact]
    public void IsOnAScreen_WhenOnlyTheTitleBarSitsUnderAPanel_IsRejected()
    {
        // WorkingArea already excludes a bottom panel, e.g. y >= 1040 is under the dock. A window positioned so
        // only its titlebar's top sliver pokes above the panel has next to no overlap with WorkingArea, even
        // though it would have overlapped plenty with the screen's raw Bounds.
        var bounds = new WindowBounds(100, PrimaryWorkingArea.Height - 2, 800, 600, IsMaximized: false);

        Assert.False(RestoredWindowBounds.IsOnAScreen(bounds, [PrimaryWorkingArea]));
    }
}
