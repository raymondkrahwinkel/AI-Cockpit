namespace Cockpit.App.Views;

/// <summary>
/// The deterministic fix for #58's stacked-at-top render desync: whether a settled
/// <c>ClaudeTtyView</c> terminal resize should actually resize the pty, or force a redraw instead.
/// Root cause (see <c>Cockpit-TTY-Glitch-RootCause-2026-07-11.md</c>): claude's TUI positions every
/// frame purely relative to the cursor row — it never re-anchors with an absolute CUP. On some
/// Wayland/KDE setups a focus/activation event triggers a transient grid change in Exclr8.Terminal
/// (e.g. 56 rows -&gt; 55 -&gt; 56) that nets back to the same size claude already has. Exclr8's own
/// <c>TerminalBuffer.Resize</c> mutates its buffer (reflow, cursor-row shift, scroll-region/viewport
/// reset) on every one of those real-but-transient changes, even though the net size is unchanged —
/// so the old code's unconditional <c>pty.Resize</c> to that unchanged size sent an identical winsize.
/// An identical winsize never raises SIGWINCH on Linux, so claude never repaints and the shifted
/// anchor stays desynced until someone clicks Redraw. Remembering the size actually last sent to the
/// pty turns that heuristic into a deterministic check: unchanged settled size after at least one
/// <c>Resized</c> event is exactly the net-zero round trip signature, and only that case needs a
/// forced redraw. Extracted as a pure helper so the decision is unit-testable without a
/// <c>DispatcherTimer</c>, mirroring <see cref="TtyAutoRedrawGate"/>.
/// </summary>
public static class TtyResizeSettleDecision
{
    public static TtyResizeSettleAction Decide(
        int lastSentColumns, int lastSentRows, int currentColumns, int currentRows) =>
        currentColumns == lastSentColumns && currentRows == lastSentRows
            ? TtyResizeSettleAction.Redraw
            : TtyResizeSettleAction.Resize;
}
