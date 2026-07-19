using System.Text.RegularExpressions;

namespace Cockpit.Infrastructure.Terminal;

/// <summary>
/// Turns the raw terminal bytes a coupled pane captured (ANSI/VT escape sequences and all) into plain text for
/// <c>read_terminal</c> (AC-34): the agent gets what a person would read on the screen, not a stream of colour codes
/// and cursor moves. Applied to the whole captured buffer at read time — never per output chunk — so an escape
/// sequence split across two pty writes is already rejoined before it is stripped.
/// <para>
/// A pragmatic strip rather than a full terminal emulation: it removes CSI (colours, cursor moves), OSC (title/
/// clipboard), and the other escape forms, folds CRLF to LF, and drops the remaining control bytes (a lone CR's
/// overwrite, a backspace, a bell) — enough to read a shell's output cleanly. It does not reconstruct a redrawn TUI
/// (htop, vim); a cell-accurate view of those is a later refinement.
/// </para>
/// </summary>
internal static class TerminalOutputSanitizer
{
    // ESC [ <params 0x30-0x3f> <intermediates 0x20-0x2f> <final 0x40-0x7e> — colours, cursor moves, erases.
    private static readonly Regex Csi = new(@"\x1b\[[\x30-\x3f]*[\x20-\x2f]*[\x40-\x7e]", RegexOptions.Compiled);

    // ESC ] ... terminated by BEL or ST (ESC \) — window title, OSC 52 clipboard, hyperlinks.
    private static readonly Regex Osc = new(@"\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)", RegexOptions.Compiled);

    // The remaining escape forms: charset designators, single-shift, and other ESC <byte> sequences. Run after CSI/OSC
    // so it never eats the ESC[ / ESC] that begins one of those.
    private static readonly Regex OtherEscape = new(@"\x1b[\x20-\x2f]*[\x30-\x7e]", RegexOptions.Compiled);

    // Remaining C0/C1 control bytes except tab (0x09) and newline (0x0a) — a lone CR, backspace, bell, DEL.
    private static readonly Regex OtherControls = new(@"[\x00-\x08\x0b-\x1f\x7f]", RegexOptions.Compiled);

    public static string ToPlainText(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return raw;
        }

        var text = Csi.Replace(raw, string.Empty);
        text = Osc.Replace(text, string.Empty);
        text = OtherEscape.Replace(text, string.Empty);
        text = text.Replace("\r\n", "\n");
        return OtherControls.Replace(text, string.Empty);
    }
}
