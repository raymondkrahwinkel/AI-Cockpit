using System.Text.RegularExpressions;

namespace Cockpit.Infrastructure.Terminal;

/// <summary>
/// Turns the raw terminal bytes a coupled pane captured (ANSI/VT escape sequences and all) into plain text for
/// <c>read_terminal</c> (AC-34): the agent gets what a person would read on the screen, not a stream of colour codes
/// and cursor moves. Applied to the whole captured buffer at read time — never per output chunk — so an escape
/// sequence split across two pty writes is already rejoined before it is stripped.
/// <para>
/// A pragmatic strip rather than a full terminal emulation: it removes CSI (colours, cursor moves), OSC (title/
/// clipboard), and the other escape forms, folds CRLF to LF, applies a lone CR as a column-0 overwrite (so a shell's
/// line redraw reads as the final text, not both drafts concatenated — AC-34), and drops the remaining control bytes
/// (a backspace, a bell) — enough to read a shell's output cleanly. It does not reconstruct a redrawn TUI
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
        text = _ApplyCarriageReturns(text);
        return OtherControls.Replace(text, string.Empty);
    }

    // A lone carriage return moves the cursor to column 0 and later characters overwrite what is already there, so a
    // shell that redraws its input line (echoes a key, then CR and reprints the whole line) reads as the final visible
    // text — not both drafts concatenated, which turned "ls" into "lls" (AC-34). Run before the control-byte strip so
    // the CR is applied, not dropped. Off the happy path (no CR present) it is a no-op.
    private static string _ApplyCarriageReturns(string text)
    {
        if (!text.Contains('\r'))
        {
            return text;
        }

        var lines = text.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            if (lines[index].Contains('\r'))
            {
                lines[index] = _OverwriteOnCarriageReturn(lines[index]);
            }
        }

        return string.Join('\n', lines);
    }

    // shortcut: a tab and a wide char each count as one cell here, matching this sanitizer's non-emulation contract —
    // a cell-accurate redraw (tab stops, double-width glyphs) stays out of scope; upgrade = a real VT parser.
    private static string _OverwriteOnCarriageReturn(string line)
    {
        var cells = new char[line.Length];
        var written = 0;
        var cursor = 0;
        foreach (var character in line)
        {
            if (character == '\r')
            {
                cursor = 0;
            }
            else
            {
                cells[cursor] = character;
                cursor++;
                if (cursor > written)
                {
                    written = cursor;
                }
            }
        }

        return new string(cells, 0, written);
    }
}
