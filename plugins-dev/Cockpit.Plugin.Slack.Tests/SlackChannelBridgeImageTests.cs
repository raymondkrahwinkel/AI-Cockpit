using Cockpit.Plugins.Abstractions.Channels;

namespace Cockpit.Plugin.Slack.Tests;

// AC-1049: what the bridge does with the files hanging off an inbound message. Nearly all of it is failure —
// a refused attachment must never take the message it came with down (criterion 5).
public class SlackChannelBridgeImageTests
{
    private const string _AllowedUserId = "U111";
    private const string _StrangerUserId = "U222";
    private const string _Url = "https://files.slack.com/private/photo.png";

    private const string _OtherUrl = _Url + "?second";

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

    private static SlackInboundFile _Image(string url = _Url, long size = 4) =>
        new("photo.png", "image/png", size, url);

    // Criterion 2: the words and the picture are one message, not two.
    [Fact]
    public async Task AnImageRidesOnTheSameMessageAsItsText()
    {
        var (bridge, gateway, sink, files) = _Build();
        files.Files[_Url] = _Bytes;

        await bridge.HandleInboundMessageAsync(_AllowedUserId, "look at this", "1", [_Image()]);

        Assert.Single(gateway.SentMessages);
        Assert.Equal("look at this", gateway.SentMessages[0].Text);
        Assert.Equal(_Bytes, Assert.Single(gateway.SentImages[0]));
        Assert.Empty(sink.Reactions);
    }

    // Criterion 3: the same thing CTRL+V does in the app — an image on its own is a message.
    [Fact]
    public async Task AnImageWithNoTextIsStillAMessage()
    {
        var (bridge, gateway, sink, files) = _Build();
        files.Files[_Url] = _Bytes;

        await bridge.HandleInboundMessageAsync(_AllowedUserId, string.Empty, "1", [_Image()]);

        Assert.Equal(string.Empty, Assert.Single(gateway.SentMessages).Text);
        Assert.Single(gateway.SentImages[0]);
        Assert.Empty(sink.Reactions);
    }

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

    [Fact]
    public async Task AFileOverTheCapIsNotEvenDownloadedAndTheTextStillArrives()
    {
        var (bridge, gateway, sink, files) = _Build();
        var huge = _Image(size: AssistantChannelImageLimits.MaxBytes + 1);

        await bridge.HandleInboundMessageAsync(_AllowedUserId, "here", "1", [huge]);

        Assert.Empty(files.Fetched);
        Assert.Equal("here", Assert.Single(gateway.SentMessages).Text);
        Assert.Contains(("1", "warning"), sink.Reactions);
    }

    // Slack without the files:read scope answers the private URL with its sign-in page, which the fetcher turns
    // into a throw — the sender is owed a sign either way.
    [Fact]
    public async Task ADownloadThatFailsCostsAReactionAndNotTheMessage()
    {
        var (bridge, gateway, sink, _) = _Build();

        await bridge.HandleInboundMessageAsync(_AllowedUserId, "here", "1", [_Image()]);

        Assert.Equal("here", Assert.Single(gateway.SentMessages).Text);
        Assert.Empty(gateway.SentImages[0]);
        Assert.Contains(("1", "warning"), sink.Reactions);
    }

    [Fact]
    public async Task StopsAtTheImageCapAndSaysSo()
    {
        var (bridge, gateway, sink, files) = _Build();

        var many = new List<SlackInboundFile>();
        for (var i = 0; i <= AssistantChannelImageLimits.MaxPerMessage; i++)
        {
            var url = $"{_Url}?{i}";
            files.Files[url] = _Bytes;
            many.Add(_Image(url));
        }

        await bridge.HandleInboundMessageAsync(_AllowedUserId, "lots", "1", many);

        Assert.Equal(AssistantChannelImageLimits.MaxPerMessage, gateway.SentImages[0].Count);
        Assert.Contains(("1", "warning"), sink.Reactions);
    }

    // The host refused them on its own side — a session whose provider cannot see images, say. Same sign.
    [Fact]
    public async Task RelaysTheHostsOwnRefusalAsAReaction()
    {
        var (bridge, gateway, sink, files) = _Build();
        files.Files[_Url] = _Bytes;
        gateway.NextResult = AssistantChannelSendResult.SentWithoutImages("the file is not an image");

        await bridge.HandleInboundMessageAsync(_AllowedUserId, "look", "1", [_Image()]);

        Assert.Contains(("1", "warning"), sink.Reactions);
    }

    // A stranger stays answered with silence (AC-1023 §3), even when there is something to complain about —
    // a ⚠️ would confirm to them that a bot is listening at all.
    [Fact]
    public async Task AStrangerGetsNoReactionEvenWhenTheirFileWasRefused()
    {
        var (bridge, gateway, sink, _) = _Build();
        gateway.NextResult = AssistantChannelSendResult.IgnoredSender();
        var pdf = new SlackInboundFile("report.pdf", "application/pdf", 4, "https://files.slack.com/private/report.pdf");

        await bridge.HandleInboundMessageAsync(_StrangerUserId, "hello?", "1", [pdf]);

        Assert.Empty(sink.Reactions);
        Assert.Empty(sink.Posted);
    }
    /// <summary>
    /// AC-1074: a dropped attachment is a dropped piece of the message, so it says so through the host. It used
    /// to go to Trace, which nothing in this app listens to, so the reason reached nobody at all.
    /// </summary>
    [Fact]
    public async Task AnAttachmentThatCannotBeFetched_IsReportedWithItsNameAndReason()
    {
        var reported = new List<string>();
        var (bridge, _, _, _) = _Build(reported.Add);

        await bridge.HandleInboundMessageAsync(_AllowedUserId, "look at this", "1", [_Image()]);

        var report = Assert.Single(reported);
        Assert.Contains("photo.png", report, StringComparison.Ordinal);
        Assert.Contains("Slack", report, StringComparison.Ordinal);
    }

    // One report for the message, not one per file: a bad token fails every attachment at once, and that would
    // be a burst of identical toasts for a single problem.
    [Fact]
    public async Task SeveralUnfetchableAttachments_AreReportedOnceForTheWholeMessage()
    {
        var reported = new List<string>();
        var (bridge, _, _, _) = _Build(reported.Add);

        await bridge.HandleInboundMessageAsync(
            _AllowedUserId, "look", "1", [_Image(), _Image(_OtherUrl)]);

        Assert.Single(reported);
    }

    // Nothing to report when every attachment arrives — the operator hears about failures only.
    [Fact]
    public async Task AnAttachmentThatArrives_ReportsNothing()
    {
        var reported = new List<string>();
        var (bridge, _, _, files) = _Build(reported.Add);
        files.Files[_Url] = _Bytes;

        await bridge.HandleInboundMessageAsync(_AllowedUserId, "look", "1", [_Image()]);

        Assert.Empty(reported);
    }
}