namespace Cockpit.App.Views;

// The action `TtyResizeSettleDecision` picked for a settled terminal resize (#58).
public enum TtyResizeSettleAction
{
    // The settled size differs from what the pty was last resized to — resize it for real; claude sees a changed winsize and repaints via SIGWINCH on its own.
    Resize,

    // The settled size is identical to what the pty already has — a net-zero resize round trip. Resizing again would send an unchanged winsize (no SIGWINCH), so force a redraw instead.
    Redraw,
}
