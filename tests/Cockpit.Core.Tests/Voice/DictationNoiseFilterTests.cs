using Cockpit.Core.Voice;

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
        Assert.Empty(DictationNoiseFilter.Strip(input));
    }

    [Fact]
    public void Strip_ThroatClearBeforeSpeech_KeepsOnlyTheSpeech()
    {
        Assert.Equal("open the settings dialog", DictationNoiseFilter.Strip("*Clears throat* open the settings dialog"));
    }

    [Fact]
    public void Strip_LeadingFiller_DropsItAndTheOrphanComma()
    {
        Assert.Equal("so I think we should ship it", DictationNoiseFilter.Strip("Um, so I think we should ship it"));
    }

    [Fact]
    public void Strip_MidSentenceFiller_DoesNotLeaveADoubleComma()
    {
        Assert.Equal("I think, we should ship it", DictationNoiseFilter.Strip("I think, um, we should ship it"));
    }

    [Fact]
    public void Strip_InlineNonSpeechTag_RemovesOnlyTheTag()
    {
        Assert.Equal("Open the file and run the tests", DictationNoiseFilter.Strip("Open the file [clears throat] and run the tests"));
    }

    [Theory]
    [InlineData("umbrella")]
    [InlineData("summary")]
    public void Strip_WordsThatMerelyContainAFiller_AreLeftIntact(string input)
    {
        Assert.Equal(input, DictationNoiseFilter.Strip(input));
    }

    [Fact]
    public void Strip_MultiWordParenthetical_IsKept_AsLikelyRealSpeech()
    {
        // Whisper's parenthesised cues are single words ("(coughs)"); a multi-word parenthesis is a person actually
        // speaking, so it must survive — only the single-token form is treated as noise.
        Assert.Equal("the result (about ten percent) is fine", DictationNoiseFilter.Strip("the result (about ten percent) is fine"));
    }

    [Fact]
    public void Strip_DutchWordEr_IsNotTreatedAsAFiller()
    {
        // "er" is a real Dutch word ("there") — the filler set deliberately excludes it.
        Assert.Equal("er is een probleem", DictationNoiseFilter.Strip("er is een probleem"));
    }

    [Fact]
    public void Strip_PlainSentence_IsUnchanged()
    {
        Assert.Equal("Open the settings dialog for me", DictationNoiseFilter.Strip("Open the settings dialog for me"));
    }
}
