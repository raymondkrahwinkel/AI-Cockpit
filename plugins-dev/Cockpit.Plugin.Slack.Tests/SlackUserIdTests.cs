using Cockpit.Plugin.Slack.Settings;

namespace Cockpit.Plugin.Slack.Tests;

public class SlackUserIdTests
{
    [Theory]
    [InlineData("U0123ABCDE")]
    [InlineData("W0123ABCDE")]
    [InlineData("U012ABC3DEF")]
    public void AMemberId_IsAccepted(string userId) =>
        Assert.True(SlackUserId.IsValid(userId));

    // AC-1048: the exact input that caused the bug — a display name, not a member id.
    [Theory]
    [InlineData("@Raymond Krahwinkel")]
    [InlineData("Raymond Krahwinkel")]
    [InlineData("raymond")]
    [InlineData("117")]
    [InlineData("u0123abcde")]
    [InlineData("U123")]
    [InlineData("")]
    public void AnythingElse_IsRejected(string userId) =>
        Assert.False(SlackUserId.IsValid(userId));

    // AC-1074: the live config held "D0BNYEX539D" — a DM conversation id in the member-id field. Telling the
    // operator which object they actually pasted is what stops them pasting it straight back in.

    // AC-1074: what SlackChannelPlugin checks a stored access list with at load, not only at save.
}
