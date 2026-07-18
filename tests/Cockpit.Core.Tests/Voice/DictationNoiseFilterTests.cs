using Cockpit.Core.Voice;
using FluentAssertions;

namespace Cockpit.Core.Tests.Voice;

/// <summary>
/// <see cref="DictationNoiseFilter"/>: drops Whisper's non-speech tags and hesitation fillers on every dictation
/// path, so a throat-clear or a bare "um" never reaches the session, while real speech (including words that merely
/// contain a filler's letters, or the Dutch word "er") is left intact.
/// </summary>
public class DictationNoiseFilterTests
{
    [Theory]
    [InlineData("*Clears throat*")]
    [InlineData("[BLANK_AUDIO]")]
    [InlineData("[Music]")]
    [InlineData("(coughs)")]
    [InlineData("um")]
    [InlineData("Uh")]
    [InlineData("ummm")]
    [InlineData("ehm")]
    [InlineData("hmm")]
    [InlineData("   ")]
    [InlineData("")]
    public void Strip_NothingButNoise_ReturnsEmpty(string input)
    {
        DictationNoiseFilter.Strip(input).Should().BeEmpty();
    }

    [Fact]
    public void Strip_ThroatClearBeforeSpeech_KeepsOnlyTheSpeech()
    {
        DictationNoiseFilter.Strip("*Clears throat* open the settings dialog")
            .Should().Be("open the settings dialog");
    }

    [Fact]
    public void Strip_LeadingFiller_DropsItAndTheOrphanComma()
    {
        DictationNoiseFilter.Strip("Um, so I think we should ship it")
            .Should().Be("so I think we should ship it");
    }

    [Fact]
    public void Strip_MidSentenceFiller_DoesNotLeaveADoubleComma()
    {
        DictationNoiseFilter.Strip("I think, um, we should ship it")
            .Should().Be("I think, we should ship it");
    }

    [Fact]
    public void Strip_InlineNonSpeechTag_RemovesOnlyTheTag()
    {
        DictationNoiseFilter.Strip("Open the file [clears throat] and run the tests")
            .Should().Be("Open the file and run the tests");
    }

    [Theory]
    [InlineData("umbrella")]
    [InlineData("summary")]
    public void Strip_WordsThatMerelyContainAFiller_AreLeftIntact(string input)
    {
        DictationNoiseFilter.Strip(input).Should().Be(input);
    }

    [Fact]
    public void Strip_MultiWordParenthetical_IsKept_AsLikelyRealSpeech()
    {
        // Whisper's parenthesised cues are single words ("(coughs)"); a multi-word parenthesis is a person actually
        // speaking, so it must survive — only the single-token form is treated as noise.
        DictationNoiseFilter.Strip("the result (about ten percent) is fine")
            .Should().Be("the result (about ten percent) is fine");
    }

    [Fact]
    public void Strip_DutchWordEr_IsNotTreatedAsAFiller()
    {
        // "er" is a real Dutch word ("there") — the filler set deliberately excludes it.
        DictationNoiseFilter.Strip("er is een probleem").Should().Be("er is een probleem");
    }

    [Fact]
    public void Strip_PlainSentence_IsUnchanged()
    {
        DictationNoiseFilter.Strip("Open the settings dialog for me")
            .Should().Be("Open the settings dialog for me");
    }
}
