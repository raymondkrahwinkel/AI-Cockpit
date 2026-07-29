using Cockpit.Core.Voice;

namespace Cockpit.Core.Tests.Voice;

/// <summary>
/// <see cref="SpeechLanguageRouter"/>: inline <c>[[nl]]</c>/<c>[[en]]</c> markers (emitted by the
/// naturalization LLM) split read-aloud text into per-language segments — the single Supertonic voice then
/// speaks each in its tagged language — while unmarked text stays a single segment in the default language.
/// </summary>
public class SpeechLanguageRouterTests
{
    [Fact]
    public void Route_NoMarkers_ReturnsOneSegmentInTheDefaultLanguage()
    {
        var segments = SpeechLanguageRouter.Route("Here is the answer.");

        Assert.Single(segments);
        Assert.Equal("en", segments[0].Language);
        Assert.Equal(new[] { "Here is the answer." }, segments[0].Sentences);
    }

    [Fact]
    public void Route_MixedDutchAndEnglish_SplitsIntoOrderedSegmentsPerLanguage()
    {
        var segments = SpeechLanguageRouter.Route("[[en]]Here is the answer. [[nl]]Dit is het antwoord.");

        Assert.Equal(2, System.Linq.Enumerable.Count(segments));
        Assert.Equal("en", segments[0].Language);
        Assert.Equal(new[] { "Here is the answer." }, segments[0].Sentences);
        Assert.Equal("nl", segments[1].Language);
        Assert.Equal(new[] { "Dit is het antwoord." }, segments[1].Sentences);
    }

    [Fact]
    public void Route_LeadingTextBeforeFirstMarker_SpeaksInTheDefaultLanguage()
    {
        var segments = SpeechLanguageRouter.Route("Intro. [[nl]]Hallo daar.");

        Assert.Equal(2, System.Linq.Enumerable.Count(segments));
        Assert.Equal("en", segments[0].Language);
        Assert.Equal(new[] { "Intro." }, segments[0].Sentences);
        Assert.Equal("nl", segments[1].Language);
        Assert.Equal(new[] { "Hallo daar." }, segments[1].Sentences);
    }

    [Fact]
    public void Route_AdjacentSameLanguageRuns_MergeIntoOneSegment()
    {
        var segments = SpeechLanguageRouter.Route("[[en]]One thing. [[en]]Another thing.");

        Assert.Single(segments);
        Assert.Equal("en", segments[0].Language);
        Assert.Equal(new[] { "One thing.", "Another thing." }, segments[0].Sentences);
    }

    [Fact]
    public void Route_UnknownMarker_FallsBackToTheDefaultLanguage()
    {
        var segments = SpeechLanguageRouter.Route("[[fr]]Bonjour tout le monde.");

        Assert.Single(segments);
        Assert.Equal("en", segments[0].Language);
    }

    [Fact]
    public void Route_WhitespaceOnly_ReturnsNoSegments()
    {
        Assert.Empty(SpeechLanguageRouter.Route("   "));
    }

    [Fact]
    public void Route_WithDutchDefault_SpeaksUnmarkedTextInDutch()
    {
        var segments = SpeechLanguageRouter.Route("Dit is het antwoord.", "nl");

        Assert.Single(segments);
        Assert.Equal("nl", segments[0].Language);
    }

    [Fact]
    public void Route_WithDutchDefault_StillHonoursAnEnglishMarker()
    {
        var segments = SpeechLanguageRouter.Route("Dit is Dutch. [[en]]This part is English.", "nl");

        Assert.Equal(2, System.Linq.Enumerable.Count(segments));
        Assert.Equal("nl", segments[0].Language);
        Assert.Equal("en", segments[1].Language);
    }
}
