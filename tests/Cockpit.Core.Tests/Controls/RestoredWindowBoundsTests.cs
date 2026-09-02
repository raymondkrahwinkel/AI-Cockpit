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

    /// <summary>
    /// Fully on screen is restored; fully off is not. The two rejections in between are the ones the old
    /// 1px-overlap check waved through: a window whose left edge sits one pixel inside the screen's right edge,
    /// and one positioned so only its titlebar's top sliver pokes above a bottom panel — plenty of overlap with
    /// the screen's raw <c>Bounds</c>, next to none with the <c>WorkingArea</c> the operator can actually reach.
    /// </summary>
    [Theory]
    [InlineData(100, 100, true)]
    [InlineData(5000, 5000, false)]
    [InlineData(1920 - 1, 100, false)]
    [InlineData(100, 1040 - 2, false)]
    public void IsOnAScreen_AcceptsOnlyAMeaningfulOverlapWithAWorkingArea(int x, int y, bool expected)
    {
        var bounds = new WindowBounds(x, y, 800, 600, IsMaximized: false);

        Assert.Equal(expected, RestoredWindowBounds.IsOnAScreen(bounds, [PrimaryWorkingArea]));
    }
}
