using Cockpit.Core.Markdown;
using FluentAssertions;

namespace Cockpit.Core.Tests.Markdown;

/// <summary>
/// The markdown subset the transcript renders: headings, paragraphs, fenced code, lists, pipe tables,
/// and inline bold/italic/code/links. Covers the shapes Claude actually emits.
/// </summary>
public class MarkdownParserTests
{
    [Fact]
    public void Heading_ParsesLevelAndText()
    {
        var block = MarkdownParser.Parse("## Wat er is").Single();

        block.Kind.Should().Be(MarkdownBlockKind.Heading);
        block.HeadingLevel.Should().Be(2);
        block.Inlines.Single().Should().Be(MarkdownInline.PlainText("Wat er is"));
    }

    [Fact]
    public void Paragraph_JoinsWrappedLines()
    {
        var block = MarkdownParser.Parse("first line\nsecond line").Single();

        block.Kind.Should().Be(MarkdownBlockKind.Paragraph);
        block.Inlines.Single().Text.Should().Be("first line second line");
    }

    [Fact]
    public void FencedCode_CapturesLanguageAndBodyVerbatim()
    {
        var block = MarkdownParser.Parse("```csharp\nvar x = 1;\nreturn x;\n```").Single();

        block.Kind.Should().Be(MarkdownBlockKind.CodeBlock);
        block.Language.Should().Be("csharp");
        block.Code.Should().Be("var x = 1;\nreturn x;");
    }

    [Fact]
    public void BulletList_ParsesEachItem()
    {
        var block = MarkdownParser.Parse("- one\n- two\n- three").Single();

        block.Kind.Should().Be(MarkdownBlockKind.List);
        block.Ordered.Should().BeFalse();
        block.Items.Select(item => item.Single().Text).Should().Equal("one", "two", "three");
    }

    [Fact]
    public void OrderedList_IsFlaggedOrdered()
    {
        var block = MarkdownParser.Parse("1. first\n2. second").Single();

        block.Kind.Should().Be(MarkdownBlockKind.List);
        block.Ordered.Should().BeTrue();
        block.Items.Should().HaveCount(2);
    }

    [Fact]
    public void Table_ParsesHeaderAndRows()
    {
        var block = MarkdownParser.Parse(
            "| Repo | Status |\n|------|--------|\n| private | your work |\n| public | official |").Single();

        block.Kind.Should().Be(MarkdownBlockKind.Table);
        block.Items.Select(cell => cell.Single().Text).Should().Equal("Repo", "Status");
        block.Rows.Should().HaveCount(2);
        block.Rows[0].Select(cell => cell.Single().Text).Should().Equal("private", "your work");
    }

    [Fact]
    public void Inlines_ParseBoldItalicCodeAndLink()
    {
        var runs = MarkdownParser.ParseInlines("plain **bold** and *italic* and `code` and [text](https://x.io).");

        runs.Should().SatisfyRespectively(
            r => { r.Kind.Should().Be(MarkdownInlineKind.Text); r.Text.Should().Be("plain "); },
            r => { r.Kind.Should().Be(MarkdownInlineKind.Bold); r.Text.Should().Be("bold"); },
            r => r.Text.Should().Be(" and "),
            r => { r.Kind.Should().Be(MarkdownInlineKind.Italic); r.Text.Should().Be("italic"); },
            r => r.Text.Should().Be(" and "),
            r => { r.Kind.Should().Be(MarkdownInlineKind.Code); r.Text.Should().Be("code"); },
            r => r.Text.Should().Be(" and "),
            r => { r.Kind.Should().Be(MarkdownInlineKind.Link); r.Text.Should().Be("text"); r.Url.Should().Be("https://x.io"); },
            r => r.Text.Should().Be("."));
    }

    [Fact]
    public void Inlines_LeaveUnmatchedMarkersAsPlainText()
    {
        var runs = MarkdownParser.ParseInlines("2 * 3 = 6 and a lone ` tick");

        string.Concat(runs.Select(r => r.Text)).Should().Be("2 * 3 = 6 and a lone ` tick");
        runs.Should().OnlyContain(r => r.Kind == MarkdownInlineKind.Text);
    }

    /// <summary>
    /// The line after one containing a pipe is tested against the table-separator pattern, and that pattern's
    /// adjacent <c>\s*</c> runs backtrack quadratically over a line of nothing but whitespace: a body of
    /// <c>"|\n" + 65_000 spaces</c> — well inside a GitHub issue's 65_536-character limit — took 2.4 seconds on a
    /// developer machine, on the UI thread, every time the operator selected that issue (AC-303). The budget here
    /// is two orders of magnitude above what a non-backtracking match costs and an order below what the
    /// backtracking one did, so it fails on the defect without being a stopwatch on a busy build agent.
    /// </summary>
    [Fact]
    public void ALineOfWhitespaceAfterAPipe_DoesNotSendTheTableSeparatorPatternQuadratic()
    {
        // Growth, not duration. A budget in milliseconds measures how busy the machine is: this failed once inside a
        // parallel run and passed three times on its own straight after, which said nothing about the parser.
        // Quadrupling the input costs roughly four times as much while the pattern stays linear and roughly sixteen
        // once it backtracks — and a loaded machine slows both measurements together, so the ratio survives what the
        // stopwatch could not. Measured here: 3.0-3.3 idle, 2.3-3.4 with a full build and test run alongside.
        const int Narrow = 16_000;
        const int Wide = 64_000;

        _FastestParseTicks(Narrow);

        var narrow = _FastestParseTicks(Narrow);
        var wide = _FastestParseTicks(Wide);
        var growth = (double)wide / narrow;

        growth.Should().BeLessThan(
            8,
            "an issue body is third-party text parsed synchronously on the UI thread, and {0}x the input took {1:F1}x the work",
            Wide / Narrow,
            growth);
    }

    // The quickest of several runs. Noise only ever adds time, so the fastest attempt is the one least disturbed by
    // whatever else the machine was doing — which is what makes the comparison above hold up under load.
    private static long _FastestParseTicks(int width)
    {
        var payload = "|\n" + new string(' ', width) + "x";
        var best = long.MaxValue;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            MarkdownParser.Parse(payload);
            elapsed.Stop();
            best = Math.Min(best, elapsed.ElapsedTicks);
        }

        return best;
    }

    [Fact]
    public void MixedDocument_ProducesBlocksInOrder()
    {
        var blocks = MarkdownParser.Parse(
            "## Title\n\nA paragraph.\n\n- item one\n- item two\n\n```\ncode\n```");

        blocks.Select(b => b.Kind).Should().Equal(
            MarkdownBlockKind.Heading,
            MarkdownBlockKind.Paragraph,
            MarkdownBlockKind.List,
            MarkdownBlockKind.CodeBlock);
    }
}
