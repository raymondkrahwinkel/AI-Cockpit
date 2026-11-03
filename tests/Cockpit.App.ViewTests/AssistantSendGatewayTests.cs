using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Assistant;
using Cockpit.Infrastructure.Assistant;
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
        session.SetAutoSubmitScheduler((_, submit) => submit());
        cockpit.Sessions.Add(session);

        var result = await gateway.SendPromptAsync(session.PaneId, "run the tests");

        Assert.True(result.Ok);
        Assert.False(result.Delivered);
        Assert.Empty(written);

        // And it is still owed: the moment the pane can take a turn, it gets the one it was handed. The pty existing
        // is one half of that (AC-760: the other is the hosted CLI's own readiness signal).
        session.PromptSink = _ => { };
        Assert.Empty(written);

        session.MarkHostedTuiReady();
        Assert.Equal(new[] { "run the tests", "\r" }, written);
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
        var sessions = new SessionRegistry();
        var cockpit = new CockpitViewModel(sessionRegistry: sessions);
        cockpit.Sessions.Clear();

        var parts = new GatewayParts(
            Substitute.For<IWorkspaceAgentGateway>(),
            Substitute.For<IAgentMessageInbox>(),
            Substitute.For<IAgentNotifyAuditLog>());

        var gateway = AssistantAgentGatewayGraph.Over(
            cockpit,
            sessions,
            Substitute.For<ISessionProfileStore>(),
            Substitute.For<IAssistantSpawnAuditLog>(),
            parts.Agents,
            parts.Inbox,
            parts.NotifyAudit,
            Substitute.For<IPluginProviderRegistry>(),
            new SessionWatcher(parts.Inbox),
            Substitute.For<IAssistantSessionHost>());

        return (gateway, cockpit, parts);
    }
}
