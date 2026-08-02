
namespace Cockpit.Plugin.TranscriptSearch.Tests;

// Windowing the snippet around the match (#9): whitespace collapse, ellipses when trimmed, and no ellipsis at the ends.
public class TranscriptSnippetTests
{
    [Fact]
    public void Build_CollapsesWhitespaceToSingleSpaces()
    {
        Assert.Equal("line one line two", TranscriptSnippet.Build("line one\n\n  line   two", "line", radius: 100));
    }

    [Fact]
    public void Build_WindowsAroundTheMatchWithEllipses()
    {
        var text = new string('a', 100) + "NEEDLE" + new string('b', 100);

        var snippet = TranscriptSnippet.Build(text, "NEEDLE", radius: 10);

        Assert.StartsWith("…", snippet);
        Assert.EndsWith("…", snippet);
        Assert.Contains("NEEDLE", snippet);
        Assert.Equal("…" + new string('a', 10) + "NEEDLE" + new string('b', 10) + "…", snippet);
    }

    [Fact]
    public void Build_NoEllipsisWhenMatchIsNearTheStartAndTextIsShort()
    {
        Assert.Equal("NEEDLE at the front", TranscriptSnippet.Build("NEEDLE at the front", "NEEDLE", radius: 60));
    }

    [Fact]
    public void Build_MatchIsCaseInsensitive()
    {
        Assert.Equal("The Login Bug", TranscriptSnippet.Build("The Login Bug", "login", radius: 60));
    }
}
