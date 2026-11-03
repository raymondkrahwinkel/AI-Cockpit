using Cockpit.Plugin.Slack.Settings;

namespace Cockpit.Plugin.Slack.Tests;

public class SlackUserIdTests
{
    // AC-1048: a display name was pasted where a member id belongs.
    [Theory]
    [InlineData("U0123ABCDE", true)]
    [InlineData("W0123ABCDE", true)]
    [InlineData("U012ABC3DEF", true)]
    [InlineData("@Raymond Krahwinkel", false)]
    [InlineData("Raymond Krahwinkel", false)]
    [InlineData("raymond", false)]
    [InlineData("117", false)]
    [InlineData("u0123abcde", false)]
    [InlineData("U123", false)]
    [InlineData("", false)]
    public void OnlyAMemberId_IsAccepted(string userId, bool expected) =>
        Assert.Equal(expected, SlackUserId.IsValid(userId));
}
