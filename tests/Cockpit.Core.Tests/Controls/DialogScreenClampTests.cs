using Cockpit.App.Controls;

namespace Cockpit.Core.Tests.Controls;

/// <summary>
/// The screen clamp keeps a desktop-sized dialog usable on a small screen: it shrinks to a fraction of the
/// working area when the designed size does not fit, and never goes below the dialog's own minimums — a
/// dialog too small to use is the failure the clamp avoids, not a fix for it.
/// </summary>
public class DialogScreenClampTests
{
    [Fact]
    public void Fit_WhenTheDesignedSizeFits_LeavesItUnchanged()
    {
        var (width, height) = DialogScreenClamp.Fit(860, 680, minWidth: 620, minHeight: 480, availableWidth: 1920, availableHeight: 1080);

        Assert.Equal(860, width);
        Assert.Equal(680, height);
    }

    [Fact]
    public void Fit_WhenTheScreenIsSmaller_ShrinksToTheScreenFraction()
    {
        var (width, height) = DialogScreenClamp.Fit(1200, 820, minWidth: 760, minHeight: 480, availableWidth: 1280, availableHeight: 720);

        Assert.Equal(1280 * 0.9, width);
        Assert.Equal(720 * 0.9, height);
    }

    [Fact]
    public void Fit_WhenTheScreenFractionFallsBelowTheMinimums_TheMinimumsWin()
    {
        var (width, height) = DialogScreenClamp.Fit(860, 680, minWidth: 620, minHeight: 480, availableWidth: 600, availableHeight: 400);

        Assert.Equal(620, width);
        Assert.Equal(480, height);
    }
}
