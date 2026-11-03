using Cockpit.Plugin.Discord.Settings;

namespace Cockpit.Plugin.Discord.Tests;

public class DiscordUserIdTests
{

    // AC-1048: the same shape of mistake as the Slack bug — a display name or tag, not a snowflake.
    [Theory]
    [InlineData("@Raymond Krahwinkel")]
    public void AnythingElse_IsRejected(string userId) =>
        Assert.False(DiscordUserId.IsValid(userId));

}
