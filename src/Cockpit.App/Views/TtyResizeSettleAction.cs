namespace Cockpit.App.Views;

/// <summary>The action <see cref="TtyResizeSettleDecision"/> picked for a settled terminal resize (#58).</summary>
public enum TtyResizeSettleAction
{
    /// <summary>The settled size differs from what the pty was last resized to — resize it for real; claude sees a changed winsize and repaints via SIGWINCH on its own.</summary>
    Resize,

    /// <summary>The settled size is identical to what the pty already has — a net-zero resize round trip. Resizing again would send an unchanged winsize (no SIGWINCH), so force a redraw instead.</summary>
    Redraw,
}
