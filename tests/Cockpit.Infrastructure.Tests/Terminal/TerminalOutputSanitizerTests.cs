using Cockpit.Infrastructure.Terminal;

namespace Cockpit.Infrastructure.Tests.Terminal;

/// <summary>Turning captured terminal bytes into plain text for read_terminal (AC-34): escapes out, readable text in.</summary>
public class TerminalOutputSanitizerTests
{
    // Built numerically so no raw control byte sits in this source file.
    private static readonly string Esc = ((char)0x1b).ToString();
    private static readonly string Bel = ((char)0x07).ToString();

    [Fact]
    public void StripsCsiColourAndCursorSequences_KeepingTheText()
    {
        var raw = $"{Esc}[31mbuild {Esc}[1mfailed{Esc}[0m{Esc}[2K on line 12";

        Assert.Equal("build failed on line 12", TerminalOutputSanitizer.ToPlainText(raw));
    }

    [Fact]
    public void StripsOscSequences_LikeAWindowTitleOrClipboardWrite()
    {
        var raw = $"{Esc}]0;my-terminal{Bel}hello";

        Assert.Equal("hello", TerminalOutputSanitizer.ToPlainText(raw));
    }

    [Fact]
    public void KeepsNewlinesAndTabs_AndFoldsCrlf()
    {
        var raw = "line1\r\nline2\tcol";

        Assert.Equal("line1\nline2\tcol", TerminalOutputSanitizer.ToPlainText(raw));
    }

    [Fact]
    public void AppliesALoneCarriageReturnAsAColumnZeroOverwrite()
    {
        // "abc\rXY" redraws from column 0 → "XYc" on a real terminal, so read_terminal must match, not concatenate.
        var raw = "abc\rXY";

        Assert.Equal("XYc", TerminalOutputSanitizer.ToPlainText(raw));
    }

    [Fact]
    public void CollapsesAShellsLineRedraw_SoAnEchoedCommandIsNotDoubled()
    {
        // The shell echoes a key then reprints the whole line from column 0; without applying the CR the two drafts
        // concatenated and read_terminal showed "lls" for "ls" (AC-34).
        var raw = "l\rls\n";

        Assert.Equal("ls\n", TerminalOutputSanitizer.ToPlainText(raw));
    }

    [Fact]
    public void PlainText_IsUnchanged()
    {
        Assert.Equal("just plain output\n", TerminalOutputSanitizer.ToPlainText("just plain output\n"));
    }
}
