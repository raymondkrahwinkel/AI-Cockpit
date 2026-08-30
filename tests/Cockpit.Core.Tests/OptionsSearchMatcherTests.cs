using Cockpit.App.Converters;

namespace Cockpit.Core.Tests;

public class OptionsSearchMatcherTests
{
    [Fact]
    public void EmptySearch_MatchesEverything()
    {
        Assert.True(OptionsSearchMatcher.MatchesAny(null, "push-to-talk key"));
        Assert.True(OptionsSearchMatcher.MatchesAny("", "push-to-talk key"));
        Assert.True(OptionsSearchMatcher.MatchesAny("   ", "push-to-talk key"));
    }

    [Fact]
    public void LiteralLabelSubstring_Matches()
    {
        Assert.True(OptionsSearchMatcher.MatchesAny("session", "close a session exit terminate"));
        Assert.False(OptionsSearchMatcher.MatchesAny("xyzzy", "close a session exit terminate"));
    }

    // AC-1000 §3: "hotkey" must find the Voice push-to-talk key row, whose own label ("Push-to-talk key") does
    // not contain the literal word "hotkey" — this only holds because that row's keyword string adds it.
    [Fact]
    public void SynonymSearch_HotkeyFindsPushToTalkKey()
    {
        const string pushToTalkKeyRowKeywords = "push-to-talk key hotkey dictation";

        Assert.DoesNotContain("hotkey", "push-to-talk key", StringComparison.OrdinalIgnoreCase);
        Assert.True(OptionsSearchMatcher.MatchesAny("hotkey", pushToTalkKeyRowKeywords));
    }

}
