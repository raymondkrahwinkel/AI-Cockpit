using Cockpit.Core.Markdown;

namespace Cockpit.Core.Tests.Markdown;

/// <summary>
/// The two shapes the knowledge base added to the shared parser (AC-1033): the explicit <c>{#id}</c> a deep
/// link aims at, and a picture on a line of its own. Both stay out of the way of everything the transcript
/// already parsed.
/// </summary>
public class MarkdownParserHelpTests
{
    [Fact]
    public void Heading_TakesItsIdFromTheExplicitAnchor()
    {
        var block = MarkdownParser.Parse("## Bot token {#bot-token}").Single();

        Assert.Equal("bot-token", block.HeadingId);
        Assert.Equal("Bot token", block.Inlines.Single().Text);
    }

    // A heading that declares none is ordinary prose: it reads the same and simply cannot be linked to,
    // which is the author's choice rather than an anchor that moves the next time the wording changes.
    [Fact]
    public void Heading_WithoutAnAnchorHasNoId()
    {
        Assert.Null(MarkdownParser.Parse("## Bot token").Single().HeadingId);
    }

    [Fact]
    public void Heading_LeavesBracesInProseAlone()
    {
        var block = MarkdownParser.Parse("Use {#bot-token} as the anchor").Single();

        Assert.Equal(MarkdownBlockKind.Paragraph, block.Kind);
        Assert.Null(block.HeadingId);
    }

    [Fact]
    public void Image_OnItsOwnLineBecomesAnImageBlock()
    {
        var block = MarkdownParser.Parse("![Privileged Gateway Intents](images/intents.png)").Single();

        Assert.Equal(MarkdownBlockKind.Image, block.Kind);
        Assert.Equal("images/intents.png", block.ImageSource);
        Assert.Equal("Privileged Gateway Intents", block.ImageAlt);
    }

    // Whether an external reference can be shown is not the parser's call — it keeps what the author wrote
    // and the renderer refuses to fetch it.
    [Fact]
    public void Image_KeepsAnExternalReferenceAsWritten()
    {
        Assert.Equal("https://example.invalid/x.png", MarkdownParser.Parse("![](https://example.invalid/x.png)").Single().ImageSource);
    }

    [Fact]
    public void Image_DoesNotSwallowTheParagraphAroundIt()
    {
        var blocks = MarkdownParser.Parse("Before\n![alt](a.png)\nAfter");

        Assert.Equal(
            [MarkdownBlockKind.Paragraph, MarkdownBlockKind.Image, MarkdownBlockKind.Paragraph],
            blocks.Select(block => block.Kind));
    }

    [Fact]
    public void Image_InsideASentenceStaysInline()
    {
        Assert.Equal(MarkdownBlockKind.Paragraph, MarkdownParser.Parse("see ![alt](a.png) here").Single().Kind);
    }
}
