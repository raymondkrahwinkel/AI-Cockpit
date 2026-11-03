using Cockpit.Plugin.Slack.Settings;

namespace Cockpit.Plugin.Slack.Tests;

public class SlackUserIdTests
{

    // AC-1048: the exact input that caused the bug — a display name, not a member id.
    [Theory]
    [InlineData("@Raymond Krahwinkel")]
    public void AnythingElse_IsRejected(string userId) =>
        Assert.False(SlackUserId.IsValid(userId));

}
