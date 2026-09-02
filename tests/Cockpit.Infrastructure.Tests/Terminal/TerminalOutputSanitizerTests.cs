using Cockpit.Infrastructure.Terminal;

namespace Cockpit.Infrastructure.Tests.Terminal;

/// <summary>Turning captured terminal bytes into plain text for read_terminal (AC-34): escapes out, readable text in.</summary>
public class TerminalOutputSanitizerTests
{
    // Built as escapes so no raw control byte sits in this source file, and const so the rows below can use them.
    private const string Esc = "\u001b";
    private const string Bel = "\u0007";

    [Theory]
    // CSI colour and cursor sequences go, the text stays.
    [InlineData(Esc + "[31mbuild " + Esc + "[1mfailed" + Esc + "[0m" + Esc + "[2K on line 12", "build failed on line 12")]
    // An OSC sequence — a window title or a clipboard write — goes the same way.
    [InlineData(Esc + "]0;my-terminal" + Bel + "hello", "hello")]
    // Newlines and tabs are kept; CRLF folds to LF.
    [InlineData("line1\r\nline2\tcol", "line1\nline2\tcol")]
    // "abc\rXY" redraws from column 0 -> "XYc" on a real terminal, so read_terminal must match, not concatenate.
    [InlineData("abc\rXY", "XYc")]
    // The shell echoes a key then reprints the whole line from column 0; without applying the CR the two drafts
    // concatenated and read_terminal showed "lls" for "ls" (AC-34).
    [InlineData("l\rls\n", "ls\n")]
    // Text with nothing to strip is handed back untouched.
    [InlineData("just plain output\n", "just plain output\n")]
    public void ToPlainText_StripsTheEscapesAndKeepsTheReadableText(string raw, string expected) =>
        Assert.Equal(expected, TerminalOutputSanitizer.ToPlainText(raw));
}
