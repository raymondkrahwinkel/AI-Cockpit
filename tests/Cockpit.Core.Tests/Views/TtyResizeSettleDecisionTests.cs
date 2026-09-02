using Cockpit.App.Views;

namespace Cockpit.Core.Tests.Views;

/// <summary>
/// The pure decision behind #58's deterministic resize-settle fix: a settled size that differs from
/// what the pty was last resized to gets a real resize (claude repaints via SIGWINCH on its own); a
/// settled size that nets back to the same size is the net-zero resize round trip that leaves claude
/// desynced unless forced to redraw.
/// </summary>
public class TtyResizeSettleDecisionTests
{
    // A settled size that differs from what the pty was last resized to gets a real resize — claude repaints via
    // SIGWINCH on its own. The last row is #58's root cause: Exclr8 fired Resized at least once during the debounce
    // window (56 -> 55 -> 56) but the settled size is identical to what the pty already has, so resizing again
    // would send an unchanged winsize, no SIGWINCH, and claude never repaints.
    [Theory]
    [InlineData(249, 56, 249, 55, TtyResizeSettleAction.Resize)]
    [InlineData(249, 56, 240, 56, TtyResizeSettleAction.Resize)]
    [InlineData(249, 56, 249, 56, TtyResizeSettleAction.Redraw)]
    public void Decide_ResizesOnAChange_AndForcesARedrawOnANetZeroRoundTrip(
        int lastSentColumns, int lastSentRows, int currentColumns, int currentRows, TtyResizeSettleAction expected)
    {
        Assert.Equal(expected, TtyResizeSettleDecision.Decide(lastSentColumns, lastSentRows, currentColumns, currentRows));
    }
}
