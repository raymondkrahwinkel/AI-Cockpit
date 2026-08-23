using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.Plugin.Slack.Tests;

public class SlackChannelBridgeTests
{
    private const string _AllowedUserId = "U111";
    private const string _StrangerUserId = "U222";

    private static AssistantChannelAccess _SingleUserAccess(string userId) =>
        AssistantChannelAccess.ForSingleUser(userId).Access!;

    private static (SlackChannelBridge Bridge, FakeAssistantChannelGateway Gateway, FakeSlackChannelSink Sink) _Build(
        AssistantChannelVerbosity verbosity = AssistantChannelVerbosity.Everything)
    {
        var (bridge, gateway, sink, _) = _BuildWithFiles(verbosity);
        return (bridge, gateway, sink);
    }

    private static (SlackChannelBridge Bridge, FakeAssistantChannelGateway Gateway, FakeSlackChannelSink Sink, FakeSlackFileFetcher Files) _BuildWithFiles(
        AssistantChannelVerbosity verbosity = AssistantChannelVerbosity.Everything)
    {
        var gateway = new FakeAssistantChannelGateway();
        var sink = new FakeSlackChannelSink();
        var files = new FakeSlackFileFetcher();
        var bridge = new SlackChannelBridge(gateway, sink, files, _SingleUserAccess(_AllowedUserId), () => verbosity);
        return (bridge, gateway, sink, files);
    }

    // AC-1025 criterion 2, the plugin-testable half of it: the host's gateway already reports a stranger's
    // message as "ignored" (AC-1023 §3) — this proves the plugin does nothing further with that: no reaction,
    // no post, nothing that would confirm to the stranger that a bot is even listening.
    [Fact]
    public async Task IgnoredSender_ProducesNoSlackActivityAtAll()
    {
        var (bridge, gateway, sink) = _Build();
        gateway.NextResult = AssistantChannelSendResult.IgnoredSender();

        await bridge.HandleInboundMessageAsync(_StrangerUserId, "hello?", messageTs: "1");

        Assert.Empty(sink.Reactions);
        Assert.Empty(sink.Posted);
        Assert.Empty(sink.Edited);
    }

    [Fact]
    public async Task AllowedSender_IsForwardedToTheGateway()
    {
        var (bridge, gateway, sink) = _Build();
        gateway.NextResult = AssistantChannelSendResult.Sent();

        await bridge.HandleInboundMessageAsync(_AllowedUserId, "hi there", messageTs: "1");

        Assert.Contains((_AllowedUserId, "hi there"), gateway.SentMessages);
        Assert.Empty(sink.Reactions);
    }

    [Fact]
    public async Task RealFailure_GetsAWarningReactionUnlikeAnIgnoredSender()
    {
        var (bridge, gateway, sink) = _Build();
        gateway.NextResult = AssistantChannelSendResult.Refused("the assistant refused");

        await bridge.HandleInboundMessageAsync(_AllowedUserId, "hi", messageTs: "42");

        var reaction = Assert.Single(sink.Reactions);
        Assert.Equal("42", reaction.MessageTs);
    }

    [Theory]
    [InlineData(AssistantChannelVerbosity.FinalAnswerOnly, AssistantChannelRowKind.ToolUse, 0)]
    [InlineData(AssistantChannelVerbosity.Everything, AssistantChannelRowKind.ToolUse, 1)]
    [InlineData(AssistantChannelVerbosity.StatusLines, AssistantChannelRowKind.ToolUse, 1)]
    public void RowRelay_HonoursTheVerbositySetting(AssistantChannelVerbosity verbosity, AssistantChannelRowKind kind, int expectedPosts)
    {
        var (_, gateway, sink) = _Build(verbosity);

        gateway.RaiseRowChanged(new AssistantChannelRow
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            Text = "some tool ran",
            Timestamp = DateTimeOffset.UtcNow,
            ToolName = "git status",
        });

        Assert.Equal(expectedPosts, sink.Posted.Count);
    }

    [Fact]
    public void RowRelay_EditsTheSameMessageOnAnUpdate()
    {
        var (_, gateway, sink) = _Build();
        var rowId = Guid.NewGuid();

        gateway.RaiseRowChanged(new AssistantChannelRow { Id = rowId, Kind = AssistantChannelRowKind.AssistantText, Text = "Working", Timestamp = DateTimeOffset.UtcNow });
        gateway.RaiseRowChanged(new AssistantChannelRow { Id = rowId, Kind = AssistantChannelRowKind.AssistantText, Text = "Working on it…", Timestamp = DateTimeOffset.UtcNow, IsUpdate = true });

        Assert.Single(sink.Posted);
        var edit = Assert.Single(sink.Edited);
        Assert.Equal("Working on it…", edit.Text);
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

        await bridge.HandleButtonAsync(SlackConsentButtonId.Approve(prompt.Id), _AllowedUserId);

        Assert.Contains((prompt.Id, ConsentOutcome.Approved, false), gateway.Responses);
        Assert.Single(sink.Edited);
    }

    // Not covered by the host's own SendAsync identity check (AC-1023 §3), since RespondToConsent takes no
    // identity at all — this is the plugin's own gap to close, and this test is what proves it does.
    [Fact]
    public async Task ButtonClick_FromAStranger_IsIgnored()
    {
        var (bridge, gateway, sink) = _Build();
        var prompt = _ConsentPrompt("do the thing");
        gateway.RaisePromptOpened(prompt);

        await bridge.HandleButtonAsync(SlackConsentButtonId.Approve(prompt.Id), _StrangerUserId);

        Assert.Empty(gateway.Responses);
        Assert.Single(sink.Posted); // only the original prompt post — no edit followed.
        Assert.Empty(sink.Edited);
    }

    [Fact]
    public async Task TextFallback_JaFromTheAllowedSender_ApprovesTheOpenPrompt()
    {
        var (bridge, gateway, _) = _Build();
        var prompt = _ConsentPrompt("do the thing");
        gateway.RaisePromptOpened(prompt);

        await bridge.HandleInboundMessageAsync(_AllowedUserId, "JA", messageTs: "1");

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

        await bridge.HandleInboundMessageAsync(_StrangerUserId, "JA", messageTs: "1");

        Assert.Empty(gateway.Responses);
        Assert.Empty(gateway.SentMessages);
    }

    // A failed post must not register the prompt as open — otherwise a "JA" typed for an unrelated reason later
    // would answer a prompt nobody in the channel ever actually saw.
    [Fact]
    public async Task ConsentPromptOpened_WhenThePostFails_NeverRegistersAsOpen()
    {
        var (bridge, gateway, sink) = _Build();
        sink.FailNextPost = true;
        var prompt = _ConsentPrompt("do the thing");

        gateway.RaisePromptOpened(prompt);
        await bridge.HandleInboundMessageAsync(_AllowedUserId, "JA", messageTs: "1");

        Assert.Empty(gateway.Responses);
        Assert.Contains((_AllowedUserId, "JA"), gateway.SentMessages);
    }

    // RowChanged/ConsentPromptOpened/ConsentPromptClosed arrive on the gateway's own thread while
    // HandleInboundMessageAsync/HandleButtonAsync arrive from SlackNet's own socket threads — this hammers the
    // shared row/prompt tracking from both sides at once and only asserts that nothing throws.
    [Fact]
    public async Task ConcurrentRowAndPromptActivity_AcrossThreads_NeverThrows()
    {
        var (bridge, gateway, _) = _Build();
        var tasks = new List<Task>();

        for (var i = 0; i < 200; i++)
        {
            var row = new AssistantChannelRow { Id = Guid.NewGuid(), Kind = AssistantChannelRowKind.AssistantText, Text = $"row {i}", Timestamp = DateTimeOffset.UtcNow };
            tasks.Add(Task.Run(() => gateway.RaiseRowChanged(row)));

            var prompt = _ConsentPrompt($"action {i}");
            tasks.Add(Task.Run(() =>
            {
                gateway.RaisePromptOpened(prompt);
                gateway.RaisePromptClosed(prompt.Id);
            }));

            var messageTs = i.ToString();
            tasks.Add(Task.Run(() => bridge.HandleInboundMessageAsync(_AllowedUserId, "JA", messageTs)));
        }

        await Task.WhenAll(tasks);
    }

    private static AssistantChannelConsentPrompt _ConsentPrompt(string action) => new(
        Guid.NewGuid(),
        new ConsentRequest("Approve this?", action, new ConsentSource(null, "slack", "Slack"), "slack.test", ConsentRisk.Dangerous),
        CanRemember: false);
}
