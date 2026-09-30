using Cockpit.Plugin.Discord.Settings;

namespace Cockpit.Plugin.Discord.Tests;

public class DiscordUserIdTests
{
    [Theory]
    [InlineData("123456789012345678")]
    [InlineData("12345678901234567")]
    public void ASnowflake_IsAccepted(string userId) =>
        Assert.True(DiscordUserId.IsValid(userId));

    // AC-1048: the same shape of mistake as the Slack bug — a display name or tag, not a snowflake.
    [Theory]
    [InlineData("@Raymond Krahwinkel")]
    [InlineData("Raymond#1234")]
    [InlineData("raymond")]
    [InlineData("117")]
    [InlineData("")]
    public void AnythingElse_IsRejected(string userId) =>
        Assert.False(DiscordUserId.IsValid(userId));

    // AC-1074: what DiscordChannelPlugin checks a stored access list with at load, not only at save — the same
    // gap that let a Slack DM conversation id sit in the member-id field unnoticed.
}
