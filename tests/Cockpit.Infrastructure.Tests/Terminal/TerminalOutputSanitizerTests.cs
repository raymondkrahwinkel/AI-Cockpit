using Cockpit.Infrastructure.Terminal;

namespace Cockpit.Infrastructure.Tests.Terminal;

/// <summary>Turning captured terminal bytes into plain text for read_terminal (AC-34): escapes out, readable text in.</summary>
public class TerminalOutputSanitizerTests
{
    // Built as escapes so no raw control byte sits in this source file, and const so the rows below can use them.
    private const string Esc = "\u001b";
    private const string Bel = "\u0007";

    [Fact]
    public void ToPlainText_StripsTheEscapesAndKeepsTheReadableText()
    {
        (string Raw, string Expected)[] cases =
        [
            (Esc + "[31mbuild " + Esc + "[1mfailed" + Esc + "[0m" + Esc + "[2K on line 12", "build failed on line 12"),
            (Esc + "]0;my-terminal" + Bel + "hello", "hello"),
            ("line1\r\nline2\tcol", "line1\nline2\tcol"),
            ("abc\rXY", "XYc"),
            ("l\rls\n", "ls\n"),
        ];
        foreach (var (raw, expected) in cases)
        {
            Assert.Equal(expected, TerminalOutputSanitizer.ToPlainText(raw));
        }
    }
}
