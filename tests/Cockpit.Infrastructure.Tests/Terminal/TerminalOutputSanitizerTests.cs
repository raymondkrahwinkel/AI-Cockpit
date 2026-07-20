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
    public void KeepsNewlinesAndTabs_FoldsCrlf_DropsLoneCarriageReturnAndOtherControls()
    {
        var raw = "line1\r\nline2\tcol\rdropped";

        TerminalOutputSanitizer.ToPlainText(raw).Should().Be("line1\nline2\tcoldropped");
    }

    [Fact]
    public void PlainText_IsUnchanged()
    {
        TerminalOutputSanitizer.ToPlainText("just plain output\n").Should().Be("just plain output\n");
    }
}
