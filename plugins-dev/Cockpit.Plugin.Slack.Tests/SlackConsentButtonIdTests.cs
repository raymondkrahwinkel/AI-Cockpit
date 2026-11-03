namespace Cockpit.Plugin.Slack.Tests;

public class SlackConsentButtonIdTests
{
    [Fact]
    public void ApproveAndDenyRoundTrip()
    {
        var promptId = Guid.NewGuid();

        Assert.True(SlackConsentButtonId.TryParse(SlackConsentButtonId.Approve(promptId), out var parsedId, out var approve));
        Assert.Equal(promptId, parsedId);
        Assert.True(approve);

        Assert.True(SlackConsentButtonId.TryParse(SlackConsentButtonId.Deny(promptId), out parsedId, out approve));
        Assert.Equal(promptId, parsedId);
        Assert.False(approve);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-consent-button")]
    [InlineData("cockpit-consent:not-a-guid:approve")]
    [InlineData("cockpit-consent:00000000000000000000000000000000:maybe")]
    [InlineData("some-other-plugin:00000000000000000000000000000000:approve")]
    public void RejectsAnythingElse(string? actionId) =>
        Assert.False(SlackConsentButtonId.TryParse(actionId, out _, out _));
}
