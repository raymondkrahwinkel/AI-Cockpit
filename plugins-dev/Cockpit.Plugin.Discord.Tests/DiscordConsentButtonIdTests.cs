namespace Cockpit.Plugin.Discord.Tests;

public class DiscordConsentButtonIdTests
{
    [Fact]
    public void ApproveAndDenyRoundTrip()
    {
        var promptId = Guid.NewGuid();

        Assert.True(DiscordConsentButtonId.TryParse(DiscordConsentButtonId.Approve(promptId), out var parsedId, out var approve));
        Assert.Equal(promptId, parsedId);
        Assert.True(approve);

        Assert.True(DiscordConsentButtonId.TryParse(DiscordConsentButtonId.Deny(promptId), out parsedId, out approve));
        Assert.Equal(promptId, parsedId);
        Assert.False(approve);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("cockpit-consent:not-a-guid:approve")]
    [InlineData("cockpit-consent:00000000000000000000000000000000:maybe")]
    [InlineData("some-other-plugin:00000000000000000000000000000000:approve")]
    public void RejectsAnythingElse(string? customId) =>
        Assert.False(DiscordConsentButtonId.TryParse(customId, out _, out _));
}
