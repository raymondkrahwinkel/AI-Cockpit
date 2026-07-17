using Cockpit.Core.Voice;
using FluentAssertions;

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

        segments.Should().ContainSingle();
        segments[0].Language.Should().Be("en");
        segments[0].Sentences.Should().Equal("Here is the answer.");
    }

    [Fact]
    public void Route_MixedDutchAndEnglish_SplitsIntoOrderedSegmentsPerLanguage()
    {
        var segments = SpeechLanguageRouter.Route("[[en]]Here is the answer. [[nl]]Dit is het antwoord.");

        segments.Should().HaveCount(2);
        segments[0].Language.Should().Be("en");
        segments[0].Sentences.Should().Equal("Here is the answer.");
        segments[1].Language.Should().Be("nl");
        segments[1].Sentences.Should().Equal("Dit is het antwoord.");
    }

    [Fact]
    public void Route_LeadingTextBeforeFirstMarker_SpeaksInTheDefaultLanguage()
    {
        var segments = SpeechLanguageRouter.Route("Intro. [[nl]]Hallo daar.");

        segments.Should().HaveCount(2);
        segments[0].Language.Should().Be("en");
        segments[0].Sentences.Should().Equal("Intro.");
        segments[1].Language.Should().Be("nl");
        segments[1].Sentences.Should().Equal("Hallo daar.");
    }

    [Fact]
    public void Route_AdjacentSameLanguageRuns_MergeIntoOneSegment()
    {
        var segments = SpeechLanguageRouter.Route("[[en]]One thing. [[en]]Another thing.");

        segments.Should().ContainSingle();
        segments[0].Language.Should().Be("en");
        segments[0].Sentences.Should().Equal("One thing.", "Another thing.");
    }

    [Fact]
    public void Route_UnknownMarker_FallsBackToTheDefaultLanguage()
    {
        var segments = SpeechLanguageRouter.Route("[[fr]]Bonjour tout le monde.");

        segments.Should().ContainSingle();
        segments[0].Language.Should().Be("en");
    }

    [Fact]
    public void Route_WhitespaceOnly_ReturnsNoSegments()
    {
        SpeechLanguageRouter.Route("   ").Should().BeEmpty();
    }
}
