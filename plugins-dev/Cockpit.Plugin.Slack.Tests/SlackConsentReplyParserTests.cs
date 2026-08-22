using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.Plugin.Slack.Tests;

public class SlackConsentReplyParserTests
{
    [Theory]
    [InlineData("ja")]
    [InlineData("JA")]
    [InlineData("  ja  ")]
    public void ParsesJaAsApproved(string text)
    {
        Assert.True(SlackConsentReplyParser.TryParse(text, out var outcome));
        Assert.Equal(ConsentOutcome.Approved, outcome);
    }

    [Theory]
    [InlineData("nee")]
    [InlineData("NEE")]
    [InlineData(" nee ")]
    public void ParsesNeeAsDenied(string text)
    {
        Assert.True(SlackConsentReplyParser.TryParse(text, out var outcome));
        Assert.Equal(ConsentOutcome.Denied, outcome);
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
