using Cockpit.App.Controls;

namespace Cockpit.Core.Tests.Controls;

/// <summary>
/// The screen clamp keeps a desktop-sized dialog usable on a small screen: it shrinks to a fraction of the
/// working area when the designed size does not fit, and never goes below the dialog's own minimums — a
/// dialog too small to use is the failure the clamp avoids, not a fix for it.
/// </summary>
public class DialogScreenClampTests
{
    // A screen that fits leaves the designed size alone; a smaller one takes 90% of the working area; and
    // below the dialog's own minimums the minimums win, because a dialog too small to use is the failure
    // the clamp avoids rather than a fix for it.
    [Theory]
    [InlineData(860, 680, 620, 480, 1920, 1080, 860, 680)]
    [InlineData(1200, 820, 760, 480, 1280, 720, 1280 * 0.9, 720 * 0.9)]
    [InlineData(860, 680, 620, 480, 600, 400, 620, 480)]
    public void Fit_ShrinksToTheScreenFraction_ButNeverBelowTheDialogsOwnMinimums(
        double designedWidth, double designedHeight, double minWidth, double minHeight,
        double availableWidth, double availableHeight, double expectedWidth, double expectedHeight)
    {
        var (width, height) = DialogScreenClamp.Fit(designedWidth, designedHeight, minWidth, minHeight, availableWidth, availableHeight);

        Assert.Equal(expectedWidth, width);
        Assert.Equal(expectedHeight, height);
    }
}
