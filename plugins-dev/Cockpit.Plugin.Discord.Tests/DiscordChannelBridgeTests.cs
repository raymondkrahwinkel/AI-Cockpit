using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.Plugin.Discord.Tests;

public class DiscordChannelBridgeTests
{
    private const string _AllowedUserId = "111";
    private const string _StrangerUserId = "222";

    private static AssistantChannelAccess _SingleUserAccess(string userId) =>
        AssistantChannelAccess.ForSingleUser(userId).Access!;

    private static (DiscordChannelBridge Bridge, FakeAssistantChannelGateway Gateway, FakeDiscordChannelSink Sink) _Build(
        AssistantChannelVerbosity verbosity = AssistantChannelVerbosity.Everything,
        Action<string>? logRefusal = null)
    {
        var (bridge, gateway, sink, _) = _BuildWithFiles(verbosity, logRefusal);
        return (bridge, gateway, sink);
    }

    private static (DiscordChannelBridge Bridge, FakeAssistantChannelGateway Gateway, FakeDiscordChannelSink Sink, FakeDiscordFileFetcher Files) _BuildWithFiles(
        AssistantChannelVerbosity verbosity = AssistantChannelVerbosity.Everything,
        Action<string>? logRefusal = null)
    {
        var gateway = new FakeAssistantChannelGateway();
        var sink = new FakeDiscordChannelSink();
        var files = new FakeDiscordFileFetcher();
        var bridge = new DiscordChannelBridge(gateway, sink, files, _SingleUserAccess(_AllowedUserId), () => verbosity, logRefusal: logRefusal);
        return (bridge, gateway, sink, files);
    }

    // AC-1024 criterion 2, the plugin-testable half of it: the host's gateway already reports a stranger's
    // message as "ignored" (AC-1023 §3) — this proves the plugin does nothing further with that: no reaction,
    // no post, nothing that would confirm to the stranger that a bot is even listening.
    [Fact]
    public async Task IgnoredSender_ProducesNoDiscordActivityAtAll()
    {
        var (bridge, gateway, sink) = _Build();
        gateway.NextResult = AssistantChannelSendResult.IgnoredSender();

        await bridge.HandleInboundMessageAsync(_StrangerUserId, "hello?", messageId: 1);

        Assert.Empty(sink.Reactions);
        Assert.Empty(sink.Posted);
        Assert.Empty(sink.Edited);
    }

    [Fact]
    public async Task AllowedSender_IsForwardedToTheGateway()
    {
        var (bridge, gateway, sink) = _Build();
        gateway.NextResult = AssistantChannelSendResult.Sent();

        await bridge.HandleInboundMessageAsync(_AllowedUserId, "hi there", messageId: 1);

        Assert.Contains((_AllowedUserId, "hi there"), gateway.SentMessages);
        Assert.Empty(sink.Reactions);
        DiscordHealthStateTests.Verify();
    }

    [Fact]
    public void ConsentPromptOpened_PostsAMessageWithTheButtonsAttached()
    {
        var (_, gateway, sink) = _Build();
        var prompt = _ConsentPrompt("rm -rf /tmp/whatever");

        gateway.RaisePromptOpened(prompt);

        var posted = Assert.Single(sink.Posted);
        Assert.Equal("rm -rf /tmp/whatever", posted.Text);
        Assert.Equal(prompt.Id, posted.ConsentPromptId);
    }

    [Fact]
    public async Task ButtonClick_FromTheAllowedSender_RespondsAndEditsTheMessage()
    {
        var (bridge, gateway, sink) = _Build();
        var prompt = _ConsentPrompt("do the thing");
        gateway.RaisePromptOpened(prompt);

        await bridge.HandleButtonAsync(DiscordConsentButtonId.Approve(prompt.Id), _AllowedUserId);

        Assert.Contains((prompt.Id, ConsentOutcome.Approved, false), gateway.Responses);
        Assert.Single(sink.Edited);
    }

    // Not covered by the host's own SendAsync identity check (AC-1023 §3), since RespondToConsent takes no
    // identity at all — this is the plugin's own gap to close, and this test is what proves it does. AC-1360: the
    // prompt stays open, and the refused click is logged with who made it.
    [Fact]
    public async Task ButtonClick_FromAStranger_IsIgnored()
    {
        var refusals = new List<string>();
        var (bridge, gateway, sink) = _Build(logRefusal: refusals.Add);
        var prompt = _ConsentPrompt("do the thing");
        gateway.RaisePromptOpened(prompt);

        await bridge.HandleButtonAsync(DiscordConsentButtonId.Approve(prompt.Id), _StrangerUserId);

        Assert.Empty(gateway.Responses);
        Assert.Single(sink.Posted); // only the original prompt post — no edit followed.
        Assert.Empty(sink.Edited);
        Assert.Contains(_StrangerUserId, Assert.Single(refusals), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TextFallback_JaFromTheAllowedSender_ApprovesTheOpenPrompt()
    {
        var (bridge, gateway, _) = _Build();
        var prompt = _ConsentPrompt("do the thing");
        gateway.RaisePromptOpened(prompt);

        await bridge.HandleInboundMessageAsync(_AllowedUserId, "JA", messageId: 1);

        Assert.Contains((prompt.Id, ConsentOutcome.Approved, false), gateway.Responses);
        Assert.Empty(gateway.SentMessages); // "JA" answers the prompt, it is never forwarded as a chat turn.
    }

    [Fact]
    public async Task TextFallback_FromAStranger_IsSilentlyIgnored()
    {
        var (bridge, gateway, _) = _Build();
        var prompt = _ConsentPrompt("do the thing");
        gateway.RaisePromptOpened(prompt);
        gateway.NextResult = AssistantChannelSendResult.IgnoredSender();

        await bridge.HandleInboundMessageAsync(_StrangerUserId, "JA", messageId: 1);

        Assert.Empty(gateway.Responses);
        Assert.Empty(gateway.SentMessages);
    }

    // AC-1360 review: a prompt closed while its post was still on its way used to be registered afterwards, dead at
    // the head of the JA/NEE queue — so every later "JA" answered nothing, not even the prompt that was really open.

    // Review point 2: a failed post must not register the prompt as open — otherwise a "JA" typed for an
    // unrelated reason later would answer a prompt nobody in the channel ever actually saw.
    [Fact]
    public async Task ConsentPromptOpened_WhenThePostFails_NeverRegistersAsOpen()
    {
        var (bridge, gateway, sink) = _Build();
        sink.FailNextPost = true;
        var prompt = _ConsentPrompt("do the thing");

        gateway.RaisePromptOpened(prompt);
        await bridge.HandleInboundMessageAsync(_AllowedUserId, "JA", messageId: 1);

        Assert.Empty(gateway.Responses);
        Assert.Contains((_AllowedUserId, "JA"), gateway.SentMessages);
    }

    // Review point 1: RowChanged/ConsentPromptOpened/ConsentPromptClosed arrive on the gateway's own thread
    // while HandleInboundMessageAsync/HandleButtonAsync arrive from Discord.NET's socket threads — this hammers
    // the shared row/prompt tracking from both sides at once and only asserts that nothing throws.

    private static AssistantChannelConsentPrompt _ConsentPrompt(string action) => new(
        Guid.NewGuid(),
        new ConsentRequest("Approve this?", action, new ConsentSource(null, "discord", "Discord"), "discord.test", ConsentRisk.Dangerous),
        CanRemember: false);
}
