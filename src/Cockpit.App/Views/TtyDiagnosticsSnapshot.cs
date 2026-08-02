using Exclr8.Terminal.Buffer;

namespace Cockpit.App.Views;

// Formats a compact single-line snapshot of Exclr8's `TerminalBuffer` render state, for the
// #58 TTY-glitch diagnostic logging in `TtyView`: cursor position, DECSTBM scroll-region
// margins, scrollback viewport offset, grid size, and any active selection's anchor/active endpoints.
//
// Decompiling Exclr8.Terminal 1.0.7 (ilspycmd) showed every one of these — `TerminalBuffer.CursorRow`/
// `TerminalBuffer.CursorCol`, `TerminalBuffer.ScrollTop`/`TerminalBuffer.ScrollBottom`,
// `TerminalBuffer.ScrollOffset`, `TerminalBuffer.Cols`/`TerminalBuffer.Rows`,
// `TerminalBuffer.Selection` — is public API on the sealed `TerminalBuffer`, reached
// through `TerminalControl.Buffer` (also public). No reflection needed: the assumption that these
// were internal did not hold, and plain property access is strictly safer than reflection here (compiler-
// checked, no member-name typos). Still wrapped in try/catch: a future Exclr8 release that renames or
// drops a member should degrade this diagnostic line to "?", not crash the TTY view it exists to debug.
public static class TtyDiagnosticsSnapshot
{
    public static string Capture(TerminalBuffer? buffer)
    {
        if (buffer is null)
        {
            return "buffer=?";
        }

        try
        {
            return $"cursor=({buffer.CursorRow},{buffer.CursorCol}) " +
                   $"region=({buffer.ScrollTop}..{buffer.ScrollBottom}) " +
                   $"scrollOffset={buffer.ScrollOffset} " +
                   $"grid={buffer.Cols}x{buffer.Rows} " +
                   $"altScreen={buffer.IsAltScreen} " +
                   $"scrollback={buffer.ScrollbackCount} " +
                   $"selection={FormatSelection(buffer.Selection)}";
        }
        catch (Exception ex)
        {
            // Defensive only (see class remarks) — every member read above is a plain public property on
            // a sealed type, so this should never actually throw; it exists so a future Exclr8 upgrade
            // that changes the surface can't take the TTY view down with it.
            return $"?(snapshot failed: {ex.GetType().Name}: {ex.Message})";
        }
    }

    private static string FormatSelection(TerminalSelection? selection) =>
        selection is { } sel
            ? $"anchor=({sel.StartRow},{sel.StartCol}) active=({sel.EndRow},{sel.EndCol}) mode={sel.Mode}"
            : "none";
}
