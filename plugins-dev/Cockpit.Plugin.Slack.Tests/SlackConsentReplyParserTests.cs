using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.Plugin.Slack.Tests;

public class SlackConsentReplyParserTests
{
    [Theory]
    [InlineData("ja", ConsentOutcome.Approved)]
    [InlineData("JA", ConsentOutcome.Approved)]
    [InlineData("  ja  ", ConsentOutcome.Approved)]
    [InlineData("nee", ConsentOutcome.Denied)]
    [InlineData("NEE", ConsentOutcome.Denied)]
    [InlineData(" nee ", ConsentOutcome.Denied)]
    public void ParsesJaAndNee(string text, ConsentOutcome expected)
    {
        Assert.True(SlackConsentReplyParser.TryParse(text, out var outcome));
        Assert.Equal(expected, outcome);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("no")]
    [InlineData("hello there")]
    [InlineData("")]
    [InlineData(null)]
    public void EverythingElseDoesNotParse(string? text) =>
        Assert.False(SlackConsentReplyParser.TryParse(text, out _));
}
