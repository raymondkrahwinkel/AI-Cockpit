using Cockpit.Plugins.Abstractions.Channels;

namespace Cockpit.Plugin.Slack.Tests;

// AC-1049: what the bridge does with the files hanging off an inbound message. Nearly all of it is failure —
// a refused attachment must never take the message it came with down (criterion 5).
public class SlackChannelBridgeImageTests
{
    private const string _AllowedUserId = "U111";
    private const string _StrangerUserId = "U222";
    private const string _Url = "https://files.slack.com/private/photo.png";

    private static readonly byte[] _Bytes = [1, 2, 3, 4];

    private static AssistantChannelAccess _SingleUserAccess(string userId) =>
        AssistantChannelAccess.ForSingleUser(userId).Access!;

    private static (SlackChannelBridge Bridge, FakeAssistantChannelGateway Gateway, FakeSlackChannelSink Sink, FakeSlackFileFetcher Files) _Build(Action<string>? reportError = null, string channelId = "<unknown>", Action<string>? reportRefusal = null)
    {
        var gateway = new FakeAssistantChannelGateway();
        var sink = new FakeSlackChannelSink();
        var files = new FakeSlackFileFetcher();
        var bridge = new SlackChannelBridge(
            gateway, sink, files, _SingleUserAccess(_AllowedUserId), () => AssistantChannelVerbosity.Everything, reportError, channelId, reportRefusal);
        return (bridge, gateway, sink, files);
    }

    // Slack without the files:read scope answers the private URL with its sign-in page, which the fetcher turns
    // into a throw — the sender is owed a sign either way.

    // The host refused them on its own side — a session whose provider cannot see images, say. Same sign.

    // A stranger stays answered with silence (AC-1023 §3), even when there is something to complain about —
    // a ⚠️ would confirm to them that a bot is listening at all. AC-1360: and nothing of theirs is downloaded.
    [Theory]
    [InlineData("photo.png", "image/png")]
    public async Task AStrangerGetsNoReaction_AndNothingOfTheirsIsDownloaded(string name, string mimeType)
    {
        var refusals = new List<string>();
        var (bridge, gateway, sink, files) = _Build(channelId: "C333", reportRefusal: refusals.Add);
        files.Files[_Url] = _Bytes;

        await bridge.HandleInboundMessageAsync(_StrangerUserId, "hello?", "1", [new SlackInboundFile(name, mimeType, 4, _Url)]);

        Assert.Empty(files.Fetched);
        Assert.Empty(gateway.SentMessages);
        Assert.Empty(gateway.SentImages);
        Assert.Empty(sink.Reactions);
        Assert.Empty(sink.Posted);
        var refusal = Assert.Single(refusals);
        Assert.Contains(_StrangerUserId, refusal);
        Assert.Contains("C333", refusal);
        Assert.DoesNotContain("hello?", refusal);
        Assert.DoesNotContain(name, refusal);
    }

}
