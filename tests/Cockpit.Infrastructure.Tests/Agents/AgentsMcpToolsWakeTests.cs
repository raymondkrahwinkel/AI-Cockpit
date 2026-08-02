using System.Text.Json.Nodes;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Infrastructure.Agents;
using Cockpit.Infrastructure.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Agents;

/// <summary>
/// Opt-in wake (AC-395) at the tool layer: who may ask for a wake, whose answer decides whether it happens, and
/// what the sender and the append-only trail are told either way.
/// <para>
/// The gateway is substituted here on purpose. This half owns two of the three refusals — a recipient that never
/// opted in, and a re-send of a message already waiting — and both must hold without the gateway being reachable
/// at all, which is what <c>DidNotReceiveWithAnyArgs</c> asserts below. The refusals that depend on the
/// recipient's live state (busy, mid-question, off the desk) are the gateway's, and are proven against real panes
/// in <c>WorkspaceAgentGatewayWakeTests</c>.
/// </para>
/// </summary>
public sealed class AgentsMcpToolsWakeTests : IDisposable
{
    private readonly IWorkspaceAgentGateway _gateway = Substitute.For<IWorkspaceAgentGateway>();
    private readonly WorkspaceAgentCoordinator _coordinator = new();
    private readonly AgentMessageInbox _inbox = new();
    private readonly AgentResourceClaims _claims = new();

    private readonly string _auditPath = Path.Combine(Path.GetTempPath(), $"agent-wake-audit-{Guid.NewGuid():N}.jsonl");

    private AgentNotifyAuditLog _Audit() => new(_auditPath, NullLogger<AgentNotifyAuditLog>.Instance);

    // As in AgentsMcpToolsTests: the AC-396 rate limit put out of the way so these tests keep asserting what they are
    // about. The wake cap is five a minute by default, and several tests here wake more than that in a burst.
    private readonly AgentLineBudget _budget = new(TimeProvider.System, TimeSpan.FromMinutes(1), 10_000, 10_000);

    private AgentsMcpTools _Tools() => new(_gateway, _coordinator, _inbox, _Audit(), _claims, _budget);

    private void _DeskWith(params string[] paneIds)
    {
        var snapshot = new WorkspaceAgentSnapshot(
            "ws-1",
            [.. paneIds.Select(paneId => new WorkspaceAgentPane(paneId, paneId, null, string.Empty, true))]);
        foreach (var paneId in paneIds)
        {
            _gateway.GetWorkspaceSnapshotAsync(paneId).Returns(Task.FromResult<WorkspaceAgentSnapshot?>(snapshot));
        }
    }

    private static JsonNode _Json(string result) => JsonNode.Parse(result)!;

    private async Task<string> _NotifyAs(string caller, string toPaneId, string kind, string body, bool urgent)
    {
        McpRequestContext.Set(caller);
        return await _Tools().NotifyAsync(toPaneId, kind, body, urgent);
    }

    public void Dispose()
    {
        McpRequestContext.Set(null);
        if (File.Exists(_auditPath))
        {
            File.Delete(_auditPath);
        }
    }

    [Fact]
    public async Task Notify_Urgent_ToAPaneThatOptedIn_WakesIt()
    {
        _DeskWith("sender", "target");
        _coordinator.SetWakeConsent("target", true);
        _gateway.TryWakeAsync("sender", "target", "branch").Returns(AgentWakeOutcome.Woken);

        var json = _Json(await _NotifyAs("sender", "target", "branch", "leave that branch alone", urgent: true));

        Assert.True(json["ok"]!.GetValue<bool>());
        Assert.True(json["wake"]!["woken"]!.GetValue<bool>());
        Assert.Equal(nameof(AgentWakeOutcome.Woken), json["wake"]!["outcome"]!.GetValue<string>());

        var entry = Assert.Single(await _Audit().ReadRecentAsync());
        Assert.True(entry.Urgent);
        Assert.Equal(AgentWakeOutcome.Woken, entry.Wake);
    }

    [Fact]
    public async Task Notify_Urgent_ToAPaneThatNeverOptedIn_DoesNotReachTheWakeAtAll()
    {
        _DeskWith("sender", "target");

        var json = _Json(await _NotifyAs("sender", "target", "branch", "leave that branch alone", urgent: true));

        // Delivered, and waiting: refusing the wake must not cost the message.
        Assert.True(json["ok"]!.GetValue<bool>());
        Assert.False(json["wake"]!["woken"]!.GetValue<bool>());
        Assert.Equal(nameof(AgentWakeOutcome.NotOptedIn), json["wake"]!["outcome"]!.GetValue<string>());
        Assert.Single(_inbox.Drain("target", int.MaxValue).Messages);

        // The consent is the gate, so nothing downstream of it may run — not merely return false.
        _ = _gateway.DidNotReceiveWithAnyArgs().TryWakeAsync(default!, default!, default!);

        var entry = Assert.Single(await _Audit().ReadRecentAsync());
        Assert.Equal(AgentWakeOutcome.NotOptedIn, entry.Wake);
    }

    [Fact]
    public async Task Notify_Urgent_AfterTheRecipientOptedBackOut_DoesNotWake()
    {
        _DeskWith("sender", "target");
        _coordinator.SetWakeConsent("target", true);
        _coordinator.SetWakeConsent("target", false);

        var json = _Json(await _NotifyAs("sender", "target", "branch", "leave that branch alone", urgent: true));

        Assert.Equal(nameof(AgentWakeOutcome.NotOptedIn), json["wake"]!["outcome"]!.GetValue<string>());
        _ = _gateway.DidNotReceiveWithAnyArgs().TryWakeAsync(default!, default!, default!);
    }

    [Fact]
    public async Task Notify_Urgent_AfterTheRecipientMadeOtherToolCalls_StillWakes()
    {
        _DeskWith("sender", "target");
        _coordinator.SetWakeConsent("target", true);
        _gateway.TryWakeAsync("sender", "target", "branch").Returns(AgentWakeOutcome.Woken);

        // Enroll is what every cockpit-agents call does for its caller, and it shares the roster entry the consent
        // lives in. An Enroll that overwrote that entry would revoke this opt-in on the recipient's very next
        // list_agents — an agent that said yes, never woken, with nothing anywhere saying why.
        _coordinator.Enroll("target");
        _coordinator.Enroll("target");

        var json = _Json(await _NotifyAs("sender", "target", "branch", "leave that branch alone", urgent: true));

        Assert.Equal(nameof(AgentWakeOutcome.Woken), json["wake"]!["outcome"]!.GetValue<string>());
    }

    [Fact]
    public async Task Notify_WithoutUrgent_ReportsNoWakeAndAttemptsNone()
    {
        _DeskWith("sender", "target");
        _coordinator.SetWakeConsent("target", true);

        var json = _Json(await _NotifyAs("sender", "target", "branch", "leave that branch alone", urgent: false));

        // Null rather than an outcome saying nothing happened: an ordinary send reads exactly as it did before wake
        // existed, so a sender that never asks about waking is never told about it either.
        Assert.Null(json["wake"]);
        _ = _gateway.DidNotReceiveWithAnyArgs().TryWakeAsync(default!, default!, default!);

        var entry = Assert.Single(await _Audit().ReadRecentAsync());
        Assert.False(entry.Urgent);
        Assert.Null(entry.Wake);
    }

    [Fact]
    public async Task Notify_UrgentTwiceWithTheSameMessage_WakesOnlyOnTheFirst()
    {
        _DeskWith("sender", "target");
        _coordinator.SetWakeConsent("target", true);
        _gateway.TryWakeAsync("sender", "target", "branch").Returns(AgentWakeOutcome.Woken);

        _ = await _NotifyAs("sender", "target", "branch", "leave that branch alone", urgent: true);
        var second = _Json(await _NotifyAs("sender", "target", "branch", "leave that branch alone", urgent: true));

        // The second send added nothing — the same message is still waiting unread. Waking again would make the
        // wake as repeatable as the sender's own retry loop, and the cap that is meant to bound this line is a
        // later ticket.
        Assert.Equal(nameof(AgentWakeOutcome.AlreadyWaiting), second["wake"]!["outcome"]!.GetValue<string>());
        Assert.False(second["wake"]!["woken"]!.GetValue<bool>());
        _ = await _gateway.Received(1).TryWakeAsync("sender", "target", "branch");
    }

    [Fact]
    public async Task Notify_UrgentResendToAPaneThatNeverOptedIn_KeepsSayingWhyItWillNotBeWoken()
    {
        _DeskWith("sender", "target");

        _ = await _NotifyAs("sender", "target", "branch", "leave that branch alone", urgent: true);
        var second = _Json(await _NotifyAs("sender", "target", "branch", "leave that branch alone", urgent: true));

        // Both refusals apply here — the recipient never opted in, and the message is a duplicate. Which one the
        // sender hears is the point: consent is a standing fact about the recipient and worth repeating, where
        // "you already said that" tells it nothing it can act on.
        Assert.Equal(nameof(AgentWakeOutcome.NotOptedIn), second["wake"]!["outcome"]!.GetValue<string>());
        _ = _gateway.DidNotReceiveWithAnyArgs().TryWakeAsync(default!, default!, default!);
    }

    [Fact]
    public async Task Notify_Urgent_WhenTheSendItselfFails_StillRecordsWhatWasAskedFor()
    {
        // The catch-all around the whole send, not the one around the wake: the failure happens before a message
        // exists, so there is no wake outcome to record — only what the sender asked for, which is the half an
        // operator reading the trail for a pane that keeps trying to wake a neighbour needs to see.
        _gateway.GetWorkspaceSnapshotAsync("sender").Returns<Task<WorkspaceAgentSnapshot?>>(_ => throw new InvalidOperationException("the desk went away"));

        var json = _Json(await _NotifyAs("sender", "target", "branch", "leave that branch alone", urgent: true));

        Assert.False(json["ok"]!.GetValue<bool>());

        var entry = Assert.Single(await _Audit().ReadRecentAsync());
        Assert.Equal(AgentNotifyOutcome.RefusedError, entry.Outcome);
        Assert.True(entry.Urgent);
        Assert.Null(entry.Wake);
    }

    [Fact]
    public async Task Notify_Urgent_WhenTheWakeThrows_KeepsTheMessageAndRecordsTheFailure()
    {
        _DeskWith("sender", "target");
        _coordinator.SetWakeConsent("target", true);
        _gateway.TryWakeAsync("sender", "target", "branch").Returns<AgentWakeOutcome>(_ => throw new InvalidOperationException("pane blew up"));

        var json = _Json(await _NotifyAs("sender", "target", "branch", "leave that branch alone", urgent: true));

        // The message was accepted before the wake was attempted, so a failing wake must not turn the whole send
        // into an error — that would tell the sender to resend something already waiting.
        Assert.True(json["ok"]!.GetValue<bool>());
        Assert.Equal(nameof(AgentWakeOutcome.Failed), json["wake"]!["outcome"]!.GetValue<string>());
        Assert.Single(_inbox.Drain("target", int.MaxValue).Messages);

        var entry = Assert.Single(await _Audit().ReadRecentAsync());
        Assert.Equal(AgentNotifyOutcome.Accepted, entry.Outcome);
        Assert.Equal(AgentWakeOutcome.Failed, entry.Wake);
    }

    [Fact]
    public async Task Notify_UrgentToItself_IsRefusedAndNothingIsWoken()
    {
        _DeskWith("sender");
        _coordinator.SetWakeConsent("sender", true);

        var json = _Json(await _NotifyAs("sender", "sender", "branch", "wake me", urgent: true));

        // Urgency must not become a way around the self-send refusal: a pane that could wake itself has a loop, not
        // a line.
        Assert.False(json["ok"]!.GetValue<bool>());
        _ = _gateway.DidNotReceiveWithAnyArgs().TryWakeAsync(default!, default!, default!);

        var entry = Assert.Single(await _Audit().ReadRecentAsync());
        Assert.Equal(AgentNotifyOutcome.RefusedSelf, entry.Outcome);
        // What was asked for is recorded even though nothing was attempted — an operator reading the trail for a
        // pane that kept trying to wake someone should see the asking, not only the refusing.
        Assert.True(entry.Urgent);
        Assert.Null(entry.Wake);
    }

    [Fact]
    public async Task Notify_UrgentToAPaneOnAnotherDesk_IsRefusedAndNothingIsWoken()
    {
        _DeskWith("sender");
        _gateway.GetWorkspaceSnapshotAsync("stranger").Returns(Task.FromResult<WorkspaceAgentSnapshot?>(
            new WorkspaceAgentSnapshot("ws-2", [new WorkspaceAgentPane("stranger", "stranger", null, string.Empty, true)])));
        _coordinator.SetWakeConsent("stranger", true);

        var json = _Json(await _NotifyAs("sender", "stranger", "branch", "wake up", urgent: true));

        // An opted-in pane in another workspace is still unreachable: consent says who may wake you, the desk says
        // who may address you at all, and the second is not weakened by the first.
        Assert.False(json["ok"]!.GetValue<bool>());
        _ = _gateway.DidNotReceiveWithAnyArgs().TryWakeAsync(default!, default!, default!);
        Assert.Empty(_inbox.Drain("stranger", int.MaxValue).Messages);
    }

    [Fact]
    public async Task SetWakeOptIn_TurnsWakingOnAndOffForTheCallersOwnPane()
    {
        _DeskWith("me");
        McpRequestContext.Set("me");

        var on = _Json(await _Tools().SetWakeOptInAsync(enabled: true));
        Assert.True(on["ok"]!.GetValue<bool>());
        Assert.True(on["wakeOptIn"]!.GetValue<bool>());
        Assert.True(_coordinator.HasWakeConsent("me"));

        var off = _Json(await _Tools().SetWakeOptInAsync(enabled: false));
        Assert.False(off["wakeOptIn"]!.GetValue<bool>());
        Assert.False(_coordinator.HasWakeConsent("me"));
    }

    [Fact]
    public async Task SetWakeOptIn_FollowsTheOperatorsSettingUntilTheSessionSaysOtherwise()
    {
        _DeskWith("me");
        McpRequestContext.Set("me");
        _coordinator.SetDefaultWakeConsent(false);

        // Enrolled by a tool call, and following the operator's answer because it has not given one of its own.
        _ = await _Tools().ListAgentsAsync();
        Assert.False(_coordinator.HasWakeConsent("me"));

        // AC-615: the operator's setting reaches a session that is already running, without it calling anything.
        _coordinator.SetDefaultWakeConsent(true);
        Assert.True(_coordinator.HasWakeConsent("me"));
        Assert.False(_coordinator.HasOwnWakeConsent("me"));
    }

    /// <summary>
    /// The override, in the direction that matters most: a session that says no stays no, whatever the operator's
    /// setting does afterwards. "Has not said" and "said no" are different states, and collapsing them would let a
    /// setting change quietly overrule a session that had opted out on purpose.
    /// </summary>
    [Fact]
    public async Task SetWakeOptIn_False_SurvivesTheOperatorTurningWakesOnAgain()
    {
        _DeskWith("me");
        McpRequestContext.Set("me");
        _coordinator.SetDefaultWakeConsent(true);

        var json = _Json(await _Tools().SetWakeOptInAsync(enabled: false));

        Assert.True(json["ok"]!.GetValue<bool>());
        Assert.True(json["yourOwnAnswer"]!.GetValue<bool>());
        Assert.False(_coordinator.HasWakeConsent("me"));

        _coordinator.SetDefaultWakeConsent(true);
        Assert.False(_coordinator.HasWakeConsent("me"));
        Assert.True(_coordinator.HasOwnWakeConsent("me"));
    }

    [Fact]
    public async Task SetWakeOptIn_WithNoVerifiedPane_Refuses()
    {
        McpRequestContext.Set(null);

        var json = _Json(await _Tools().SetWakeOptInAsync(enabled: true));

        // With nothing to attribute the request to there is no session whose consent this would be — and consent
        // recorded against the wrong pane is a standing permission to wake a session that never agreed.
        Assert.False(json["ok"]!.GetValue<bool>());
        _ = _gateway.DidNotReceiveWithAnyArgs().GetWorkspaceSnapshotAsync(default!);
    }

    [Fact]
    public async Task SetWakeOptIn_FromAPaneWithNoWorkspace_Refuses()
    {
        // A plain terminal pane carries a pane id and an MCP key but is not an agent session; the gateway answers
        // null for it. Consent from one would be a standing permission to inject turns into something with no
        // agent on the other end.
        _gateway.GetWorkspaceSnapshotAsync("terminal").Returns(Task.FromResult<WorkspaceAgentSnapshot?>(null));
        McpRequestContext.Set("terminal");

        var json = _Json(await _Tools().SetWakeOptInAsync(enabled: true));

        Assert.False(json["ok"]!.GetValue<bool>());
        Assert.False(_coordinator.HasWakeConsent("terminal"));
    }

    [Fact]
    public async Task SetWakeOptIn_WhenTheDeskLookupFails_RefusesRatherThanRecordingConsent()
    {
        // The same class as the notify catch-all: a failure while deciding whether this caller may answer at all
        // must not end with its consent stored anyway. Consent recorded on a path the host could not verify is a
        // standing permission to wake something nobody checked.
        _gateway.GetWorkspaceSnapshotAsync("me").Returns<Task<WorkspaceAgentSnapshot?>>(_ => throw new InvalidOperationException("the desk went away"));
        McpRequestContext.Set("me");

        var json = _Json(await _Tools().SetWakeOptInAsync(enabled: true));

        Assert.False(json["ok"]!.GetValue<bool>());
        Assert.False(_coordinator.HasWakeConsent("me"));
    }

    [Fact]
    public async Task ListAgents_ReportsEachPanesOwnWakeOptIn()
    {
        _DeskWith("me", "opted-in", "not-opted-in");
        // The operator's setting off, so what the roster reports per pane is each pane's own answer and not one
        // default showing through three rows (AC-615).
        _coordinator.SetDefaultWakeConsent(false);
        _coordinator.SetWakeConsent("opted-in", true);
        McpRequestContext.Set("me");

        var agents = _Json(await _Tools().ListAgentsAsync())["agents"]!.AsArray();

        var byPane = agents.ToDictionary(
            agent => agent!["paneId"]!.GetValue<string>(),
            agent => agent!["wakeOptIn"]!.GetValue<bool>(),
            StringComparer.Ordinal);

        // Asserted per pane rather than "at least one is true": a roster that reported the caller's own answer for
        // every row would pass a looser check while telling every sender the opposite of the truth about someone.
        Assert.True(byPane["opted-in"]);
        Assert.False(byPane["not-opted-in"]);
        Assert.False(byPane["me"]);
    }

    [Fact]
    public async Task Forget_DropsWakeConsentWithTheRoster()
    {
        _DeskWith("target");
        _coordinator.SetDefaultWakeConsent(false);
        _coordinator.SetWakeConsent("target", true);

        _coordinator.Forget("target");

        Assert.False(_coordinator.HasWakeConsent("target"));

        // And a pane id that comes back comes back with no answer of its own — following the operator's setting like
        // any fresh session, not carrying the previous session's yes.
        _coordinator.Enroll("target");
        Assert.False(_coordinator.HasOwnWakeConsent("target"));
        Assert.False(_coordinator.HasWakeConsent("target"));

        await Task.CompletedTask;
    }
}
