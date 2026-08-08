using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Assistant;
using Cockpit.Infrastructure.Sessions;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The host half of the assistant's two reaching-in tools. <c>send_message</c> rides the agent line's existing
/// inbox rather than a second one, and <c>send_prompt</c> rides the spawn brief's own delivery — so what these
/// cover is which panes may be reached at all, and that a turn handed to a session that is still coming up is
/// reported as held rather than as sent.
/// </summary>
[Collection("avalonia")]
public class AssistantSendGatewayTests
{
    private const string TargetPane = "pane-worker";

    // ── send_prompt ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public Task APromptAddressedAtTheAssistantsOwnPane_IsRefused() => HeadlessAvalonia.RunAsync(async () =>
    {
        var (gateway, _, _) = _Gateway();

        var result = await gateway.SendPromptAsync(AssistantIdentity.PaneId, "run the tests");

        Assert.False(result.Ok);
        Assert.Contains("my own session", result.Error!, StringComparison.Ordinal);
    });

    [Fact]
    public Task APromptForAPaneThatIsNotThere_IsRefused() => HeadlessAvalonia.RunAsync(async () =>
    {
        var (gateway, _, _) = _Gateway();

        var result = await gateway.SendPromptAsync("pane-that-closed", "run the tests");

        Assert.False(result.Ok);
        Assert.Contains("pane-that-closed", result.Error!, StringComparison.Ordinal);
    });

    /// <summary>
    /// A plain terminal has a pane id and nobody on the other end reading it — the same refusal
    /// <see cref="IAssistantAgentGateway.StopAsync"/> makes, because what may be handed a turn has to be what could
    /// be seen as an agent in the first place.
    /// </summary>
    [Fact]
    public Task APromptForATerminalPane_IsRefused_AndNothingIsTyped() => HeadlessAvalonia.RunAsync(async () =>
    {
        var (gateway, cockpit, _) = _Gateway();
        var written = new List<string>();
        var terminal = new TtyViewModel { ShowPluginHeaderItems = false, PromptSink = written.Add };
        cockpit.Sessions.Add(terminal);

        var result = await gateway.SendPromptAsync(terminal.PaneId, "run the tests");

        Assert.False(result.Ok);
        Assert.Empty(written);
    });

    [Fact]
    public Task APromptForALiveAgentSession_IsSubmittedAndReportedAsDelivered() => HeadlessAvalonia.RunAsync(async () =>
    {
        var (gateway, cockpit, _) = _Gateway();
        var written = new List<string>();
        // Both seams, because the real view wires both: it subscribes VoiceTranscriptReady when the data context
        // attaches and assigns PromptSink once the pty is actually up, and both are the same write into that pty.
        var session = new TtyViewModel { PromptSink = written.Add };
        session.VoiceTranscriptReady += written.Add;
        session.SetAutoSubmitScheduler(submit => submit());
        cockpit.Sessions.Add(session);

        var result = await gateway.SendPromptAsync(session.PaneId, "run the tests");

        Assert.True(result.Ok);
        Assert.True(result.Delivered);
        Assert.Equal(new[] { "run the tests", "\r" }, written);
    });

    /// <summary>
    /// The half that makes this worth having a field for: a session the assistant only just spawned cannot take a
    /// turn yet, and the turn must neither vanish nor be reported as sent. It waits, and <c>delivered</c> says so.
    /// </summary>
    [Fact]
    public Task APromptForASessionThatIsStillComingUp_IsHeld_AndNotReportedAsDelivered() => HeadlessAvalonia.RunAsync(async () =>
    {
        var (gateway, cockpit, _) = _Gateway();
        var written = new List<string>();
        var session = new TtyViewModel();
        session.VoiceTranscriptReady += written.Add;
        session.SetAutoSubmitScheduler(submit => submit());
        cockpit.Sessions.Add(session);

        var result = await gateway.SendPromptAsync(session.PaneId, "run the tests");

        Assert.True(result.Ok);
        Assert.False(result.Delivered);
        Assert.Empty(written);

        // And it is still owed: the moment the pane can take a turn, it gets the one it was handed.
        session.PromptSink = _ => { };
        Assert.Equal(new[] { "run the tests", "\r" }, written);
    });

    // ── send_message ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public Task AMessageAddressedAtTheAssistantsOwnPane_IsRefused_AndNothingIsDelivered() => HeadlessAvalonia.RunAsync(async () =>
    {
        var (gateway, _, parts) = _Gateway();

        var result = await gateway.SendMessageAsync(AssistantIdentity.PaneId, "heads-up", "the branch moved");

        Assert.False(result.Ok);
        parts.Inbox.DidNotReceiveWithAnyArgs().Deliver(default!, default!, default!, default!);
    });

    /// <summary>
    /// The addressee is validated against the agent line's own answer for that pane rather than against a desk the
    /// caller sits on — which is what lets the assistant, who sits on no desk, reach every desk without any change
    /// to what <c>notify</c> lets an ordinary agent reach.
    /// </summary>
    [Fact]
    public Task AMessageForAPaneTheAgentLineDoesNotResolve_IsRefused_AndNothingIsDelivered() => HeadlessAvalonia.RunAsync(async () =>
    {
        var (gateway, _, parts) = _Gateway();
        parts.Agents.GetWorkspaceSnapshotAsync(TargetPane).Returns((WorkspaceAgentSnapshot?)null);

        var result = await gateway.SendMessageAsync(TargetPane, "heads-up", "the branch moved");

        Assert.False(result.Ok);
        parts.Inbox.DidNotReceiveWithAnyArgs().Deliver(default!, default!, default!, default!);
    });

    [Fact]
    public Task AMessageForAnAgentOnAnyDesk_LandsInTheSameInboxNotifyUses_StampedAsTheAssistant() => HeadlessAvalonia.RunAsync(async () =>
    {
        var (gateway, _, parts) = _Gateway();
        _Roster(parts, deliversAtTurnStart: true);
        parts.Inbox.Deliver(AssistantIdentity.PaneId, TargetPane, "heads-up", "the branch moved").Returns(
            new AgentMessageDelivery(
                AgentMessageDeliveryOutcome.Delivered,
                new AgentMessage("msg-1", AssistantIdentity.PaneId, TargetPane, "heads-up", "the branch moved", DateTimeOffset.UtcNow)));

        var result = await gateway.SendMessageAsync(TargetPane, "heads-up", "the branch moved");

        Assert.True(result.Ok);
        Assert.Equal("msg-1", result.MessageId);
        Assert.True(result.DeliversAtTurnStart);
        Assert.Equal("worker", result.SessionName);

        // The sender is the assistant's own pane and not something a caller could name.
        parts.Inbox.Received(1).Deliver(AssistantIdentity.PaneId, TargetPane, "heads-up", "the branch moved");
    });

    /// <summary>
    /// A recipient that only sees mail when it goes looking is not a recipient that has been told. The field is
    /// reported straight off the roster so the assistant cannot round it up into "I let them know".
    /// </summary>
    [Fact]
    public Task AMessageToAPaneWithNoTurnStartDelivery_SaysSo() => HeadlessAvalonia.RunAsync(async () =>
    {
        var (gateway, _, parts) = _Gateway();
        _Roster(parts, deliversAtTurnStart: false);
        parts.Inbox.Deliver(AssistantIdentity.PaneId, TargetPane, "heads-up", "the branch moved").Returns(
            new AgentMessageDelivery(
                AgentMessageDeliveryOutcome.Delivered,
                new AgentMessage("msg-1", AssistantIdentity.PaneId, TargetPane, "heads-up", "the branch moved", DateTimeOffset.UtcNow)));

        var result = await gateway.SendMessageAsync(TargetPane, "heads-up", "the branch moved");

        Assert.True(result.Ok);
        Assert.False(result.DeliversAtTurnStart);
    });

    [Fact]
    public Task AMessageForARecipientWhoseInboxIsFull_IsRefused() => HeadlessAvalonia.RunAsync(async () =>
    {
        var (gateway, _, parts) = _Gateway();
        _Roster(parts, deliversAtTurnStart: true);
        parts.Inbox.Deliver(AssistantIdentity.PaneId, TargetPane, "heads-up", "the branch moved").Returns(
            new AgentMessageDelivery(AgentMessageDeliveryOutcome.RecipientInboxFull, null));

        var result = await gateway.SendMessageAsync(TargetPane, "heads-up", "the branch moved");

        Assert.False(result.Ok);
        Assert.Contains("full", result.Error!, StringComparison.Ordinal);
    });

    private static void _Roster(GatewayParts parts, bool deliversAtTurnStart) =>
        parts.Agents.GetWorkspaceSnapshotAsync(TargetPane).Returns(new WorkspaceAgentSnapshot(
            "ws-release",
            [new WorkspaceAgentPane(TargetPane, "worker", "Opus", string.Empty, deliversAtTurnStart)]));

    private sealed record GatewayParts(
        IWorkspaceAgentGateway Agents,
        IAgentMessageInbox Inbox,
        IAgentNotifyAuditLog NotifyAudit);

    private static (AssistantAgentGateway Gateway, CockpitViewModel Cockpit, GatewayParts Parts) _Gateway()
    {
        var cockpit = new CockpitViewModel();
        cockpit.Sessions.Clear();

        var parts = new GatewayParts(
            Substitute.For<IWorkspaceAgentGateway>(),
            Substitute.For<IAgentMessageInbox>(),
            Substitute.For<IAgentNotifyAuditLog>());

        var gateway = new AssistantAgentGateway(
            cockpit,
            Substitute.For<ISessionProfileStore>(),
            Substitute.For<IAssistantSpawnAuditLog>(),
            parts.Agents,
            parts.Inbox,
            parts.NotifyAudit,
            Substitute.For<IPluginProviderRegistry>());

        return (gateway, cockpit, parts);
    }
}
