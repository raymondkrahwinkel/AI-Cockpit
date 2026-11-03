namespace Cockpit.Plugin.Slack.Tests;

public class SlackGatewayConnectionTests
{
    private const string _Channel = "C123";
    private const string _DmChannel = "D123";

    [Theory]
    [InlineData("message_changed")]
    [InlineData("message_deleted")]
    [InlineData("channel_join")]
    [InlineData("channel_leave")]
    [InlineData("bot_message")]
    [InlineData("bot_add")]
    [InlineData("pinned_item")]
    [InlineData("channel_topic")]
    [InlineData("channel_convert_to_private")]
    [InlineData("app_conversation_leave")]
    [InlineData("file_comment")]
    [InlineData("tombstone")]
    public void IgnoresBotAndSystemSubtypes(string subtype) =>
        Assert.False(SlackGatewayConnection.ShouldHandle(botId: null, subtype, _DmChannel, "im", _DmChannel));

    [Theory]
    [InlineData(null, _DmChannel, "im", true)]
    [InlineData(null, _Channel, "channel", false)]
    [InlineData("B999", _DmChannel, "im", false)]
    public void AcceptsOnlyHumanMessagesFromTheOwnerDm(string? botId, string channel, string channelType, bool expected) =>
        Assert.Equal(expected, SlackGatewayConnection.ShouldHandle(botId, subtype: null, channel, channelType, _DmChannel));
}
