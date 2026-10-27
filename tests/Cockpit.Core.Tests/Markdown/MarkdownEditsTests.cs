using Cockpit.Core.Markdown;

namespace Cockpit.Core.Tests.Markdown;

// AC-1753 (layer 2, unit): a toolbar action rewrites the signs it is about and leaves every other character alone.
public class MarkdownEditsTests
{
    [Fact]
    public void ToolbarActions_ChangeOnlyTheSignsTheyAreAbout()
    {
        Assert.Equal("## item", MarkdownEdits.SetBlock("- item", MarkdownBlockStyle.Heading2));
        Assert.Equal("item", MarkdownEdits.SetBlock("## item", MarkdownBlockStyle.Heading2));
        Assert.Equal("- [ ] buy **milk**", MarkdownEdits.SetBlock("1. buy **milk**", MarkdownBlockStyle.Task));
        Assert.Equal("> a  b ", MarkdownEdits.SetBlock("a  b ", MarkdownBlockStyle.Quote));

        Assert.Equal(new MarkdownWrapResult("say **hello** now", 6, 5), MarkdownEdits.Wrap("say hello now", 4, 9, MarkdownWrapStyle.Bold));
        Assert.Equal(new MarkdownWrapResult("a *text*", 3, 4), MarkdownEdits.Wrap("a ", 2, 2, MarkdownWrapStyle.Italic));
        Assert.Equal(new MarkdownWrapResult("see [docs](https://)", 11, 8), MarkdownEdits.Wrap("see docs", 4, 8, MarkdownWrapStyle.Link));

        Assert.Equal(new MarkdownLineBreak("- one", "- two", 2), MarkdownEdits.ContinueList("- onetwo", 5));
        Assert.Equal(new MarkdownLineBreak("  9. x", "  10. ", 6), MarkdownEdits.ContinueList("  9. x", 6));
        Assert.Equal(new MarkdownLineBreak("- [x] done", "- [ ] ", 6), MarkdownEdits.ContinueList("- [x] done", 10));
        Assert.Equal(new MarkdownLineBreak(string.Empty, null, 0), MarkdownEdits.ContinueList("- ", 2));
        Assert.Null(MarkdownEdits.ContinueList("plain text", 5));
    }
}
