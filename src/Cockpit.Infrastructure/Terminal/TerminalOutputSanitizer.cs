using System.Text.RegularExpressions;

namespace Cockpit.Infrastructure.Terminal;

// Turns the raw terminal bytes a coupled pane captured into plain text for `read_terminal` (AC-34). Applied to
// the whole captured buffer at read time — never per output chunk — so a split escape sequence is rejoined first.
// A pragmatic strip, not a full terminal emulation: it does not reconstruct a redrawn TUI (htop, vim).
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

    // A lone CR moves the cursor to column 0, so a shell's line redraw reads as the final text, not both
    // drafts concatenated (was "ls" -> "lls", AC-34). Run before the control-byte strip so the CR is applied.
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
