using Cockpit.Core.Voice;
using FluentAssertions;

namespace Cockpit.Core.Tests.Voice;

/// <summary>
/// <see cref="SpeechLanguageRouter"/>: inline <c>[[nl]]</c>/<c>[[en]]</c> markers (emitted by the
/// naturalization LLM) split read-aloud text into per-language segments routed to the matching Piper
/// voice, while unmarked text stays a single segment in the English/primary voice (pre-routing behaviour).
/// </summary>
public class SpeechLanguageRouterTests
{
    private const string English = "en_US-lessac-medium";
    private const string Dutch = "nl_NL-ronnie-medium";

    [Fact]
    public void Route_NoMarkers_ReturnsOneSegmentInTheEnglishVoice()
    {
        var segments = SpeechLanguageRouter.Route("Here is the answer.", English, Dutch);

        segments.Should().ContainSingle();
        segments[0].VoiceId.Should().Be(English);
        segments[0].Sentences.Should().Equal("Here is the answer.");
    }

    [Fact]
    public void Route_MixedDutchAndEnglish_SplitsIntoOrderedSegmentsPerLanguage()
    {
        var segments = SpeechLanguageRouter.Route("[[en]]Here is the answer. [[nl]]Dit is het antwoord.", English, Dutch);

        segments.Should().HaveCount(2);
        segments[0].VoiceId.Should().Be(English);
        segments[0].Sentences.Should().Equal("Here is the answer.");
        segments[1].VoiceId.Should().Be(Dutch);
        segments[1].Sentences.Should().Equal("Dit is het antwoord.");
    }

    [Fact]
    public void Route_LeadingTextBeforeFirstMarker_SpeaksInTheEnglishVoice()
    {
        var segments = SpeechLanguageRouter.Route("Intro. [[nl]]Hallo daar.", English, Dutch);

        segments.Should().HaveCount(2);
        segments[0].VoiceId.Should().Be(English);
        segments[0].Sentences.Should().Equal("Intro.");
        segments[1].VoiceId.Should().Be(Dutch);
        segments[1].Sentences.Should().Equal("Hallo daar.");
    }

    [Fact]
    public void Route_AdjacentSameLanguageRuns_MergeIntoOneSegment()
    {
        var segments = SpeechLanguageRouter.Route("[[en]]One thing. [[en]]Another thing.", English, Dutch);

        segments.Should().ContainSingle();
        segments[0].VoiceId.Should().Be(English);
        segments[0].Sentences.Should().Equal("One thing.", "Another thing.");
    }

    [Fact]
    public void Route_UnknownMarker_FallsBackToTheEnglishVoice()
    {
        var segments = SpeechLanguageRouter.Route("[[fr]]Bonjour tout le monde.", English, Dutch);

        segments.Should().ContainSingle();
        segments[0].VoiceId.Should().Be(English);
    }

    [Fact]
    public void Route_WhitespaceOnly_ReturnsNoSegments()
    {
        SpeechLanguageRouter.Route("   ", English, Dutch).Should().BeEmpty();
    }
}
