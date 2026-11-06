using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.Plugin.Discord.Tests;

public class DiscordConsentReplyParserTests
{
    [Theory]
    [InlineData("ja", ConsentOutcome.Approved)]
    [InlineData("JA", ConsentOutcome.Approved)]
    [InlineData("  ja  ", ConsentOutcome.Approved)]
    [InlineData("nee", ConsentOutcome.Denied)]
    [InlineData("NEE", ConsentOutcome.Denied)]
    [InlineData(" nee ", ConsentOutcome.Denied)]
    [InlineData("yes", null)]
    [InlineData("no", null)]
    [InlineData("hello there", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ParsesOnlyJaAndNee(string? text, ConsentOutcome? expected)
    {
        var parsed = DiscordConsentReplyParser.TryParse(text, out var outcome);

        Assert.Equal(expected, parsed ? outcome : null);
    }
}
