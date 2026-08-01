using Cockpit.Core.Markdown;

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

        Assert.Equal(MarkdownBlockKind.Heading, block.Kind);
        Assert.Equal(2, block.HeadingLevel);
        Assert.Equal(MarkdownInline.PlainText("Wat er is"), block.Inlines.Single());
    }

    [Fact]
    public void Paragraph_JoinsWrappedLines()
    {
        var block = MarkdownParser.Parse("first line\nsecond line").Single();

        Assert.Equal(MarkdownBlockKind.Paragraph, block.Kind);
        Assert.Equal("first line second line", block.Inlines.Single().Text);
    }

    [Fact]
    public void FencedCode_CapturesLanguageAndBodyVerbatim()
    {
        var block = MarkdownParser.Parse("```csharp\nvar x = 1;\nreturn x;\n```").Single();

        Assert.Equal(MarkdownBlockKind.CodeBlock, block.Kind);
        Assert.Equal("csharp", block.Language);
        Assert.Equal("var x = 1;\nreturn x;", block.Code);
    }

    [Fact]
    public void BulletList_ParsesEachItem()
    {
        var block = MarkdownParser.Parse("- one\n- two\n- three").Single();

        Assert.Equal(MarkdownBlockKind.List, block.Kind);
        Assert.False(block.Ordered);
        Assert.Equal(new[] { "one", "two", "three" }, block.Items.Select(item => item.Single().Text));
    }

    [Fact]
    public void OrderedList_IsFlaggedOrdered()
    {
        var block = MarkdownParser.Parse("1. first\n2. second").Single();

        Assert.Equal(MarkdownBlockKind.List, block.Kind);
        Assert.True(block.Ordered);
        Assert.Equal(2, System.Linq.Enumerable.Count(block.Items));
    }

    [Fact]
    public void Table_ParsesHeaderAndRows()
    {
        var block = MarkdownParser.Parse(
            "| Repo | Status |\n|------|--------|\n| private | your work |\n| public | official |").Single();

        Assert.Equal(MarkdownBlockKind.Table, block.Kind);
        Assert.Equal(new[] { "Repo", "Status" }, block.Items.Select(cell => cell.Single().Text));
        Assert.Equal(2, System.Linq.Enumerable.Count(block.Rows));
        Assert.Equal(new[] { "private", "your work" }, block.Rows[0].Select(cell => cell.Single().Text));
    }

    [Fact]
    public void Inlines_ParseBoldItalicCodeAndLink()
    {
        var runs = MarkdownParser.ParseInlines("plain **bold** and *italic* and `code` and [text](https://x.io).");

        Assert.Collection(
            runs,
            r => { Assert.Equal(MarkdownInlineKind.Text, r.Kind); Assert.Equal("plain ", r.Text); },
            r => { Assert.Equal(MarkdownInlineKind.Bold, r.Kind); Assert.Equal("bold", r.Text); },
            r => Assert.Equal(" and ", r.Text),
            r => { Assert.Equal(MarkdownInlineKind.Italic, r.Kind); Assert.Equal("italic", r.Text); },
            r => Assert.Equal(" and ", r.Text),
            r => { Assert.Equal(MarkdownInlineKind.Code, r.Kind); Assert.Equal("code", r.Text); },
            r => Assert.Equal(" and ", r.Text),
            r => { Assert.Equal(MarkdownInlineKind.Link, r.Kind); Assert.Equal("text", r.Text); Assert.Equal("https://x.io", r.Url); },
            r => Assert.Equal(".", r.Text));
    }

    [Fact]
    public void Inlines_LeaveUnmatchedMarkersAsPlainText()
    {
        var runs = MarkdownParser.ParseInlines("2 * 3 = 6 and a lone ` tick");

        Assert.Equal("2 * 3 = 6 and a lone ` tick", string.Concat(runs.Select(r => r.Text)));
        Assert.All(runs, r => Assert.True(r.Kind == MarkdownInlineKind.Text));
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

        Assert.True(
            growth < 8,
            string.Format(
                "an issue body is third-party text parsed synchronously on the UI thread, and {0}x the input took {1:F1}x the work",
                Wide / Narrow,
                growth));
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
    public void BareUrl_InAParagraph_BecomesALinkToItself()
    {
        var runs = MarkdownParser.Parse("PR created: https://github.com/a/b/pull/365").Single().Inlines;

        var link = Assert.Single(runs, r => r.Kind == MarkdownInlineKind.Link);
        Assert.Equal("https://github.com/a/b/pull/365", link.Text);
        Assert.Equal("https://github.com/a/b/pull/365", link.Url);
    }

    [Fact]
    public void BareUrl_InAHeadingListItemAndTableCell_BecomesALink()
    {
        var blocks = MarkdownParser.Parse(
            "# see https://x.io/h\n\n- see https://x.io/l\n\n| a |\n|---|\n| see https://x.io/t |");

        Assert.Equal(
            new[] { "https://x.io/h", "https://x.io/l", "https://x.io/t" },
            blocks.SelectMany(_AllInlines).Where(r => r.Kind == MarkdownInlineKind.Link).Select(r => r.Url!).ToArray());
    }

    /// <summary>The reported shape: <c>**[#365](url)**</c> showed its own syntax on screen, bold and all.</summary>
    [Fact]
    public void LinkInsideBold_IsOneBoldLinkAndNotLiteralSyntax()
    {
        var runs = MarkdownParser.ParseInlines("Pushed: **[#365](https://x.io/pull/365)**");

        var link = Assert.Single(runs, r => r.Kind == MarkdownInlineKind.Link);
        Assert.Equal("#365", link.Text);
        Assert.Equal("https://x.io/pull/365", link.Url);
        Assert.True(link.IsBold);
        Assert.DoesNotContain("[", string.Concat(runs.Select(r => r.Text)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("*[#365](https://x.io/p)*")]
    [InlineData("[*#365*](https://x.io/p)")]
    public void ItalicAndLink_NestEitherWayRound(string markdown)
    {
        var link = Assert.Single(MarkdownParser.ParseInlines(markdown));

        Assert.Equal(MarkdownInlineKind.Link, link.Kind);
        Assert.Equal("#365", link.Text);
        Assert.Equal("https://x.io/p", link.Url);
        Assert.True(link.IsItalic);
    }

    [Fact]
    public void BoldInsideALink_KeepsBothTheLinkAndTheWeight()
    {
        var link = Assert.Single(MarkdownParser.ParseInlines("[**#365**](https://x.io/p)"));

        Assert.Equal(MarkdownInlineKind.Link, link.Kind);
        Assert.Equal("#365", link.Text);
        Assert.Equal("https://x.io/p", link.Url);
        Assert.True(link.IsBold);
    }

    [Fact]
    public void EmphasisAroundAMixedRun_AppliesToEveryPartWithoutMergingThem()
    {
        var runs = MarkdownParser.ParseInlines("**before [in](https://x.io/p) after**");

        Assert.Equal("before in after", string.Concat(runs.Select(r => r.Text)));
        Assert.All(runs, r => Assert.True(r.IsBold));
        Assert.Equal("https://x.io/p", Assert.Single(runs, r => r.Kind == MarkdownInlineKind.Link).Url);
    }

    /// <summary>
    /// One character too many still renders as a link and then 404s, so the boundary is asserted on the URL
    /// itself rather than on "a link was found".
    /// </summary>
    [Theory]
    [InlineData("see (https://x.io/a) here", "https://x.io/a")]
    [InlineData("see https://x.io/a. Next", "https://x.io/a")]
    [InlineData("see https://x.io/a, next", "https://x.io/a")]
    [InlineData("see https://x.io/a; next", "https://x.io/a")]
    [InlineData("see https://x.io/a! next", "https://x.io/a")]
    [InlineData("see https://x.io/Foo_(bar) here", "https://x.io/Foo_(bar)")]
    [InlineData("see https://x.io/a?q=1&r=2 here", "https://x.io/a?q=1&r=2")]
    public void BareUrl_StopsBeforeThePunctuationTheSentenceOwns(string markdown, string expected)
    {
        var link = Assert.Single(MarkdownParser.ParseInlines(markdown), r => r.Kind == MarkdownInlineKind.Link);

        Assert.Equal(expected, link.Url);
        Assert.Equal(expected, link.Text);
    }

    [Fact]
    public void BareUrl_InACodeSpanOrFencedCode_StaysText()
    {
        var blocks = MarkdownParser.Parse("run `curl https://x.io/a` first\n\n```\nGET https://x.io/b\n```");

        Assert.DoesNotContain(blocks.SelectMany(_AllInlines), r => r.Kind == MarkdownInlineKind.Link);
        Assert.Contains("https://x.io/b", blocks.Last().Code, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownLink_IsNotPickedUpASecondTimeByTheAutolinker()
    {
        var runs = MarkdownParser.ParseInlines("[https://x.io/a](https://x.io/a)");

        var link = Assert.Single(runs);
        Assert.Equal(MarkdownInlineKind.Link, link.Kind);
        Assert.Equal("https://x.io/a", link.Text);
    }

    [Theory]
    [InlineData("a [text]( unclosed")]
    [InlineData("a **bold that never closes")]
    [InlineData("a *lone marker and _another")]
    [InlineData("bare https:// with no host")]
    public void HalfFinishedMarkup_RendersAsTextAndDoesNotThrow(string markdown)
    {
        var runs = MarkdownParser.ParseInlines(markdown);

        Assert.Equal(markdown, string.Concat(runs.Select(r => r.Text)));
    }

    /// <summary>
    /// The renderer finds the link under the cursor by summing <see cref="MarkdownInline.Text"/> lengths into
    /// (start, length, url) ranges over the composed text (<c>MarkdownView._InlineTextBlock</c>). Nesting is
    /// where those offsets can slip a character: everything still renders, and a click opens the neighbouring
    /// link. So this walks every character position and asserts the URL that position resolves to.
    /// </summary>
    [Fact]
    public void EveryCharacterPosition_ResolvesToTheLinkPrintedUnderIt()
    {
        var (text, links) = _Compose("go to **[one](https://x.io/1)** or *[two](https://x.io/2)*, else https://x.io/3.");

        Assert.Equal("go to one or two, else https://x.io/3.", text);

        foreach (var (label, url) in new[] { ("one", "https://x.io/1"), ("two", "https://x.io/2"), ("https://x.io/3", "https://x.io/3") })
        {
            var at = text.IndexOf(label, StringComparison.Ordinal);
            Assert.All(Enumerable.Range(at, label.Length), position => Assert.Equal(url, _UrlAt(links, position)));
            Assert.Null(_UrlAt(links, at - 1));
            Assert.Null(_UrlAt(links, at + label.Length));
        }
    }

    /// <summary>Mirrors how <c>MarkdownView</c> turns inline runs into text plus clickable ranges.</summary>
    private static (string Text, IReadOnlyList<(int Start, int Length, string Url)> Links) _Compose(string markdown)
    {
        var text = new System.Text.StringBuilder();
        var links = new List<(int Start, int Length, string Url)>();

        foreach (var inline in MarkdownParser.Parse(markdown).SelectMany(_AllInlines))
        {
            if (inline.Kind == MarkdownInlineKind.Link && !string.IsNullOrEmpty(inline.Url))
            {
                links.Add((text.Length, inline.Text.Length, inline.Url));
            }

            text.Append(inline.Text);
        }

        return (text.ToString(), links);
    }

    private static string? _UrlAt(IReadOnlyList<(int Start, int Length, string Url)> links, int position)
    {
        foreach (var link in links)
        {
            if (position >= link.Start && position < link.Start + link.Length)
            {
                return link.Url;
            }
        }

        return null;
    }

    private static IEnumerable<MarkdownInline> _AllInlines(MarkdownBlock block) =>
        block.Inlines
            .Concat(block.Items.SelectMany(item => item))
            .Concat(block.Rows.SelectMany(row => row.SelectMany(cell => cell)));

    [Fact]
    public void MixedDocument_ProducesBlocksInOrder()
    {
        var blocks = MarkdownParser.Parse(
            "## Title\n\nA paragraph.\n\n- item one\n- item two\n\n```\ncode\n```");

        Assert.Equal(
            new[]
            {
                MarkdownBlockKind.Heading,
                MarkdownBlockKind.Paragraph,
                MarkdownBlockKind.List,
                MarkdownBlockKind.CodeBlock,
            },
            blocks.Select(b => b.Kind));
    }
}
