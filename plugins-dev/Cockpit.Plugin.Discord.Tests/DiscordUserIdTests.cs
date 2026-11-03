using Cockpit.Plugin.Discord.Settings;

namespace Cockpit.Plugin.Discord.Tests;

public class DiscordUserIdTests
{
    // AC-1048: the same shape of mistake as the Slack bug — a display name or tag, not a snowflake, is rejected.
    // AC-1074: what DiscordChannelPlugin checks a stored access list with at load, not only at save — the same
    // gap that let a Slack DM conversation id sit in the member-id field unnoticed.
    [Theory]
    [InlineData("123456789012345678", true)]
    [InlineData("12345678901234567", true)]
    [InlineData("@Raymond Krahwinkel", false)]
    [InlineData("Raymond#1234", false)]
    [InlineData("raymond", false)]
    [InlineData("117", false)]
    [InlineData("", false)]
    public void OnlyASnowflake_IsAccepted(string userId, bool valid) =>
        Assert.Equal(valid, DiscordUserId.IsValid(userId));
}
