namespace Cockpit.Plugin.Slack.Tests;

public class SlackGatewayConnectionTests
{
    private const string _Channel = "C123";
    private const string _DmChannel = "D123";

    [Theory]
    [InlineData(null, _DmChannel, "im", true)]
    [InlineData(null, _Channel, "channel", false)]
    [InlineData("B999", _DmChannel, "im", false)]
    public void AcceptsOnlyHumanMessagesFromTheOwnerDm(string? botId, string channel, string channelType, bool expected) =>
        Assert.Equal(expected, SlackGatewayConnection.ShouldHandle(botId, subtype: null, channel, channelType, _DmChannel));
}
