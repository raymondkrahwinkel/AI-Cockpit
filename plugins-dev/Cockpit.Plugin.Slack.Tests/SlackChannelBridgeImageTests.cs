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

    private static (SlackChannelBridge Bridge, FakeAssistantChannelGateway Gateway, FakeSlackChannelSink Sink, FakeSlackFileFetcher Files) _Build(Action<string>? reportError = null)
    {
        var gateway = new FakeAssistantChannelGateway();
        var sink = new FakeSlackChannelSink();
        var files = new FakeSlackFileFetcher();
        var bridge = new SlackChannelBridge(
            gateway, sink, files, _SingleUserAccess(_AllowedUserId), () => AssistantChannelVerbosity.Everything, reportError);
        return (bridge, gateway, sink, files);
    }

    // Criterion 2: the words and the picture are one message, not two.

    // Criterion 3: the same thing CTRL+V does in the app — an image on its own is a message.

    // Criterion 5, the whole of it: a PDF is not passed on, the sender can see that, and the sentence they wrote
    // underneath it still reaches the assistant.
    [Fact]
    public async Task ANonImageIsRefusedAndTheTextStillArrives()
    {
        var (bridge, gateway, sink, _) = _Build();
        var pdf = new SlackInboundFile("report.pdf", "application/pdf", 4, "https://files.slack.com/private/report.pdf");

        await bridge.HandleInboundMessageAsync(_AllowedUserId, "what do you make of this", "1", [pdf]);

        Assert.Equal("what do you make of this", Assert.Single(gateway.SentMessages).Text);
        Assert.Empty(gateway.SentImages[0]);
        Assert.Contains(("1", "warning"), sink.Reactions);
    }

    // Slack without the files:read scope answers the private URL with its sign-in page, which the fetcher turns
    // into a throw — the sender is owed a sign either way.

    // The host refused them on its own side — a session whose provider cannot see images, say. Same sign.

    // A stranger stays answered with silence (AC-1023 §3), even when there is something to complain about —
    // a ⚠️ would confirm to them that a bot is listening at all. AC-1360: and nothing of theirs is downloaded.
    [Theory]
    [InlineData("report.pdf", "application/pdf")]
    [InlineData("photo.png", "image/png")]
    public async Task AStrangerGetsNoReaction_AndNothingOfTheirsIsDownloaded(string name, string mimeType)
    {
        var (bridge, gateway, sink, files) = _Build();
        files.Files[_Url] = _Bytes;
        gateway.NextResult = AssistantChannelSendResult.IgnoredSender();

        await bridge.HandleInboundMessageAsync(_StrangerUserId, "hello?", "1", [new SlackInboundFile(name, mimeType, 4, _Url)]);

        Assert.Empty(files.Fetched);
        Assert.Empty(Assert.Single(gateway.SentImages));
        Assert.Empty(sink.Reactions);
        Assert.Empty(sink.Posted);
    }

    // AC-1074: a dropped attachment is a dropped piece of the message, so it says so through the host. It used
    // to go to Trace, which nothing in this app listens to, so the reason reached nobody at all.

    // One report for the message, not one per file: a bad token fails every attachment at once, and that would
    // be a burst of identical toasts for a single problem.

    // Nothing to report when every attachment arrives — the operator hears about failures only.
}