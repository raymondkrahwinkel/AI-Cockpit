using Exclr8.Terminal.Buffer;

namespace Cockpit.App.Views;

// #58: single-line TerminalBuffer render-state snapshot for TTY-glitch diagnostics in TtyView.
// Decompiling Exclr8.Terminal 1.0.7 confirmed these members are public, so plain property access
// is used, not reflection; try/catch guards a future Exclr8 rename.
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
            // Defensive only (see class remarks) — a future Exclr8 upgrade that changes the surface
            // must not take the TTY view down with it.
            return $"?(snapshot failed: {ex.GetType().Name}: {ex.Message})";
        }
    }

    private static string FormatSelection(TerminalSelection? selection) =>
        selection is { } sel
            ? $"anchor=({sel.StartRow},{sel.StartCol}) active=({sel.EndRow},{sel.EndCol}) mode={sel.Mode}"
            : "none";
}
