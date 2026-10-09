using Cockpit.Core.Markdown;

namespace Cockpit.Core.Tests.Updates;

/// <summary>
/// The changelog wraps its bullets with an indented continuation line (AC-1515). The parser must keep that line in the
/// bullet, or "What's new" shows the rest of the sentence as a paragraph outside the list.
/// </summary>
public class ChangelogMarkdownTests
{
    [Fact]
    public void AWrappedBullet_KeepsItsContinuationLines_InTheItem()
    {
        var blocks = MarkdownParser.Parse("- fixed: no browser tab for an MCP server\n  whose sign-in has run out.\n  It shows a notice.\n- second");

        var list = Assert.Single(blocks);
        Assert.Equal(MarkdownBlockKind.List, list.Kind);
        Assert.Equal(2, list.Items.Count);
        Assert.Equal(
            "fixed: no browser tab for an MCP server whose sign-in has run out. It shows a notice.",
            string.Concat(list.Items[0].Select(inline => inline.Text)));
    }
}
