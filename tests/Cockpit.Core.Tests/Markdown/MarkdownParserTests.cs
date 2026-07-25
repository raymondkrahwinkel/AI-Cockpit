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
        var payload = "|\n" + new string(' ', 65_000) + "x";

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        MarkdownParser.Parse(payload);
        elapsed.Stop();

        elapsed.ElapsedMilliseconds.Should().BeLessThan(250,
            "an issue body is third-party text and parsing runs synchronously on the UI thread");
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
