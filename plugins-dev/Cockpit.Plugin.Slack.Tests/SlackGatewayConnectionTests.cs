namespace Cockpit.Plugin.Slack.Tests;

public class SlackGatewayConnectionTests
{
    private const string Channel = "C123";

    [Theory]
    [InlineData(null)]
    [InlineData("file_share")]
    [InlineData("thread_broadcast")]
    [InlineData("me_message")]
    public void HandlesRealUserMessages(string? subtype) =>
        Assert.True(SlackGatewayConnection.ShouldHandle(botId: null, subtype, Channel, Channel));

    [Theory]
    [InlineData("message_changed")]
    [InlineData("message_deleted")]
    [InlineData("channel_join")]
    [InlineData("channel_leave")]
    [InlineData("bot_message")]
    [InlineData("pinned_item")]
    [InlineData("channel_topic")]
    [InlineData("tombstone")]
    public void IgnoresBotAndSystemSubtypes(string subtype) =>
        Assert.False(SlackGatewayConnection.ShouldHandle(botId: null, subtype, Channel, Channel));

    [Fact]
    public void IgnoresOurOwnOutboundMessages() =>
        Assert.False(SlackGatewayConnection.ShouldHandle(botId: "B999", subtype: null, Channel, Channel));

    [Fact]
    public void IgnoresAnotherChannel() =>
        Assert.False(SlackGatewayConnection.ShouldHandle(botId: null, subtype: null, "C456", Channel));
}
