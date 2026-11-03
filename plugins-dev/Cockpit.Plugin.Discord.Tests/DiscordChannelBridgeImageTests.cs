using Cockpit.Plugins.Abstractions.Channels;

namespace Cockpit.Plugin.Discord.Tests;

// AC-1049: what the bridge does with the attachments on an inbound message. Nearly all of it is failure — a
// refused attachment must never take the message it came with down (criterion 5).
public class DiscordChannelBridgeImageTests
{
    private const string _AllowedUserId = "111";
    private const string _StrangerUserId = "222";
    private const string _Url = "https://cdn.discordapp.com/attachments/1/2/photo.png";

    private static readonly byte[] _Bytes = [1, 2, 3, 4];

    private static AssistantChannelAccess _SingleUserAccess(string userId) =>
        AssistantChannelAccess.ForSingleUser(userId).Access!;

    // A stranger stays answered with silence (AC-1023 §3), even when there is something to complain about. And
    // AC-1360: nothing of theirs is downloaded — the allowlist is checked before any attachment is fetched.
    [Theory]
    [InlineData("photo.png", "image/png")]
    public async Task AStranger_GetsNoReaction_AndNothingOfTheirsIsDownloaded(string name, string mimeType)
    {
        var refusals = new List<string>();
        var gateway = new FakeAssistantChannelGateway();
        var sink = new FakeDiscordChannelSink();
        var files = new FakeDiscordFileFetcher();
        files.Files[_Url] = _Bytes;
        var bridge = new DiscordChannelBridge(
            gateway, sink, files, _SingleUserAccess(_AllowedUserId), () => AssistantChannelVerbosity.Everything, logRefusal: refusals.Add);

        await bridge.HandleInboundMessageAsync(_StrangerUserId, "hello?", 1, [new DiscordInboundFile(name, mimeType, 4, _Url)]);

        Assert.Empty(files.Fetched);
        Assert.Empty(gateway.SentMessages);
        Assert.Empty(sink.Reactions);
        Assert.Empty(sink.Posted);
        Assert.Contains(_StrangerUserId, Assert.Single(refusals), StringComparison.Ordinal);
    }

}
