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



    /// <summary>
    /// The override, in the direction that matters most: a session that says no stays no, whatever the operator's
    /// setting does afterwards. "Has not said" and "said no" are different states, and collapsing them would let a
    /// setting change quietly overrule a session that had opted out on purpose.
    /// </summary>


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

}
