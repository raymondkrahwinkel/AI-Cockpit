using Cockpit.App.Views;
using FluentAssertions;

namespace Cockpit.Core.Tests.Views;

/// <summary>
/// The pure decision behind #58's deterministic resize-settle fix: a settled size that differs from
/// what the pty was last resized to gets a real resize (claude repaints via SIGWINCH on its own); a
/// settled size that nets back to the same size is the net-zero resize round trip that leaves claude
/// desynced unless forced to redraw.
/// </summary>
public class TtyResizeSettleDecisionTests
{
    [Fact]
    public void Decide_SizeChanged_ReturnsResize()
    {
        TtyResizeSettleDecision.Decide(lastSentColumns: 249, lastSentRows: 56, currentColumns: 249, currentRows: 55)
            .Should().Be(TtyResizeSettleAction.Resize);
    }

    [Fact]
    public void Decide_ColumnsChangedOnly_ReturnsResize()
    {
        TtyResizeSettleDecision.Decide(lastSentColumns: 249, lastSentRows: 56, currentColumns: 240, currentRows: 56)
            .Should().Be(TtyResizeSettleAction.Resize);
    }

    [Fact]
    public void Decide_SizeUnchanged_ReturnsRedraw()
    {
        // The net-zero round trip (#58 root cause): Exclr8 fired Resized at least once during the
        // debounce window (e.g. 56 -> 55 -> 56), but the settled size is identical to what the pty
        // already has — resizing again would send an unchanged winsize, no SIGWINCH, claude never
        // repaints.
        TtyResizeSettleDecision.Decide(lastSentColumns: 249, lastSentRows: 56, currentColumns: 249, currentRows: 56)
            .Should().Be(TtyResizeSettleAction.Redraw);
    }
}
