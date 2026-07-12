using Cockpit.Core.Transcripts;
using FluentAssertions;

namespace Cockpit.Core.Tests.Transcripts;

/// <summary>Windowing the snippet around the match (#9): whitespace collapse, ellipses when trimmed, and no ellipsis at the ends.</summary>
public class TranscriptSnippetTests
{
    [Fact]
    public void Build_CollapsesWhitespaceToSingleSpaces()
    {
        TranscriptSnippet.Build("line one\n\n  line   two", "line", radius: 100)
            .Should().Be("line one line two");
    }

    [Fact]
    public void Build_WindowsAroundTheMatchWithEllipses()
    {
        var text = new string('a', 100) + "NEEDLE" + new string('b', 100);

        var snippet = TranscriptSnippet.Build(text, "NEEDLE", radius: 10);

        snippet.Should().StartWith("…");
        snippet.Should().EndWith("…");
        snippet.Should().Contain("NEEDLE");
        snippet.Should().Be("…" + new string('a', 10) + "NEEDLE" + new string('b', 10) + "…");
    }

    [Fact]
    public void Build_NoEllipsisWhenMatchIsNearTheStartAndTextIsShort()
    {
        TranscriptSnippet.Build("NEEDLE at the front", "NEEDLE", radius: 60)
            .Should().Be("NEEDLE at the front");
    }

    [Fact]
    public void Build_MatchIsCaseInsensitive()
    {
        TranscriptSnippet.Build("The Login Bug", "login", radius: 60)
            .Should().Be("The Login Bug");
    }
}
