using Exclr8.Terminal.Buffer;

namespace Cockpit.App.Views;

/// <summary>
/// Formats a compact single-line snapshot of Exclr8's <see cref="TerminalBuffer"/> render state, for the
/// #58 TTY-glitch diagnostic logging in <c>TtyView</c>: cursor position, DECSTBM scroll-region
/// margins, scrollback viewport offset, grid size, and any active selection's anchor/active endpoints.
///
/// <para>Decompiling Exclr8.Terminal 1.0.7 (ilspycmd) showed every one of these — <see cref="TerminalBuffer.CursorRow"/>/
/// <see cref="TerminalBuffer.CursorCol"/>, <see cref="TerminalBuffer.ScrollTop"/>/<see cref="TerminalBuffer.ScrollBottom"/>,
/// <see cref="TerminalBuffer.ScrollOffset"/>, <see cref="TerminalBuffer.Cols"/>/<see cref="TerminalBuffer.Rows"/>,
/// <see cref="TerminalBuffer.Selection"/> — is public API on the sealed <c>TerminalBuffer</c>, reached
/// through <c>TerminalControl.Buffer</c> (also public). No reflection needed: the assumption that these
/// were internal did not hold, and plain property access is strictly safer than reflection here (compiler-
/// checked, no member-name typos). Still wrapped in try/catch: a future Exclr8 release that renames or
/// drops a member should degrade this diagnostic line to "?", not crash the TTY view it exists to debug.
/// </para>
/// </summary>
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
