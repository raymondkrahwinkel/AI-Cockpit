using Cockpit.Infrastructure.Terminal;
using FluentAssertions;

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

        TerminalOutputSanitizer.ToPlainText(raw).Should().Be("build failed on line 12");
    }

    [Fact]
    public void StripsOscSequences_LikeAWindowTitleOrClipboardWrite()
    {
        var raw = $"{Esc}]0;my-terminal{Bel}hello";

        TerminalOutputSanitizer.ToPlainText(raw).Should().Be("hello");
    }

    [Fact]
    public void KeepsNewlinesAndTabs_AndFoldsCrlf()
    {
        var raw = "line1\r\nline2\tcol";

        TerminalOutputSanitizer.ToPlainText(raw).Should().Be("line1\nline2\tcol");
    }

    [Fact]
    public void AppliesALoneCarriageReturnAsAColumnZeroOverwrite()
    {
        // "abc\rXY" redraws from column 0 → "XYc" on a real terminal, so read_terminal must match, not concatenate.
        var raw = "abc\rXY";

        TerminalOutputSanitizer.ToPlainText(raw).Should().Be("XYc");
    }

    [Fact]
    public void CollapsesAShellsLineRedraw_SoAnEchoedCommandIsNotDoubled()
    {
        // The shell echoes a key then reprints the whole line from column 0; without applying the CR the two drafts
        // concatenated and read_terminal showed "lls" for "ls" (AC-34).
        var raw = "l\rls\n";

        TerminalOutputSanitizer.ToPlainText(raw).Should().Be("ls\n");
    }

    [Fact]
    public void PlainText_IsUnchanged()
    {
        TerminalOutputSanitizer.ToPlainText("just plain output\n").Should().Be("just plain output\n");
    }
}
