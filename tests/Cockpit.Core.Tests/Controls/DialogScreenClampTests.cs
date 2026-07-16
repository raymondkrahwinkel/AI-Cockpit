using Cockpit.App.Controls;
using FluentAssertions;

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

        width.Should().Be(860);
        height.Should().Be(680);
    }

    [Fact]
    public void Fit_WhenTheScreenIsSmaller_ShrinksToTheScreenFraction()
    {
        var (width, height) = DialogScreenClamp.Fit(1200, 820, minWidth: 760, minHeight: 480, availableWidth: 1280, availableHeight: 720);

        width.Should().Be(1280 * 0.9);
        height.Should().Be(720 * 0.9);
    }

    [Fact]
    public void Fit_WhenTheScreenFractionFallsBelowTheMinimums_TheMinimumsWin()
    {
        var (width, height) = DialogScreenClamp.Fit(860, 680, minWidth: 620, minHeight: 480, availableWidth: 600, availableHeight: 400);

        width.Should().Be(620);
        height.Should().Be(480);
    }
}
