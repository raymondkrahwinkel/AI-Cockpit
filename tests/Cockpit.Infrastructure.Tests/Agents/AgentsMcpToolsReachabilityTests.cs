using System.Text.Json.Nodes;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Infrastructure.Agents;
using Cockpit.Infrastructure.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Agents;

/// <summary>
/// What a sender can find out about whether its message will ever be read (AC-614). Everything here exists because
/// of one thing measured in the field on 2026-07-31: a message sent to a pane that had never touched the MCP was
/// accepted, delivered, and reported in a reply whose every field read like success — while nothing was ever going
/// to collect it. The sender waited.
/// <para>
/// Three separate facts came out of that, and they are tested apart because they answer different questions:
/// <c>lastInboxReadUtc</c> (has anyone ever picked up), the <c>unreachable</c> warning on the send itself (will
/// anyone pick this one up), and the difference between a pane that left and a pane id that never named anything.
/// </para>
/// </summary>
public sealed class AgentsMcpToolsReachabilityTests : IDisposable
{
    private readonly IWorkspaceAgentGateway _gateway = Substitute.For<IWorkspaceAgentGateway>();
    private readonly WorkspaceAgentCoordinator _coordinator = new();
    private readonly AgentMessageInbox _inbox = new();
    private readonly AgentResourceClaims _claims = new();
    private readonly AgentLineBudget _budget = new(TimeProvider.System, TimeSpan.FromMinutes(1), 10_000, 10_000);

    private readonly string _auditPath = Path.Combine(Path.GetTempPath(), $"agent-reach-audit-{Guid.NewGuid():N}.jsonl");

    private AgentNotifyAuditLog _Audit() => new(_auditPath, NullLogger<AgentNotifyAuditLog>.Instance);

    private AgentsMcpTools _Tools() => new(_gateway, _coordinator, _inbox, _Audit(), _claims, _budget);

    /// <summary>Puts panes on one desk. <paramref name="deliversAtTurnStart"/> is the SDK/TTY split this all turns on.</summary>
    private void _DeskWith(bool deliversAtTurnStart, params string[] paneIds)
    {
        var snapshot = new WorkspaceAgentSnapshot(
            "ws-1",
            [.. paneIds.Select(paneId => new WorkspaceAgentPane(paneId, paneId, null, string.Empty, deliversAtTurnStart))]);
        foreach (var paneId in paneIds)
        {
            _gateway.GetWorkspaceSnapshotAsync(paneId).Returns(Task.FromResult<WorkspaceAgentSnapshot?>(snapshot));
        }
    }

    private static JsonNode _Json(string result) => JsonNode.Parse(result)!;

    private async Task<JsonNode> _NotifyAs(string caller, string toPaneId, string body = "the migration is running")
    {
        McpRequestContext.Set(caller);
        return _Json(await _Tools().NotifyAsync(toPaneId, "heads-up", body));
    }

    public void Dispose()
    {
        McpRequestContext.Set(null);
        if (File.Exists(_auditPath))
        {
            File.Delete(_auditPath);
        }
    }

    /// <summary>
    /// AC-527 criterion 6, per branch. The claim has to match what actually happens: a pane reported as reachable
    /// that receives nothing is a worse failure than one that honestly says nobody can reach it.
    /// </summary>
    [Fact]
    public async Task ListAgents_ReachableVia_ReportsTurnStartForAPaneWhoseTurnsCarryMail()
    {
        _DeskWith(deliversAtTurnStart: true, "pane-1", "pane-2");
        McpRequestContext.Set("pane-1");

        var seen = _Json(await _Tools().ListAgentsAsync())["agents"]!
            .AsArray()
            .First(agent => agent!["paneId"]!.GetValue<string>() == "pane-2")!;

        Assert.Equal("turnStart", seen["reachableVia"]!.GetValue<string>());
    }

    /// <summary>
    /// The route this whole sub exists for: no turn-start delivery, but the pane calls cockpit tools, so the host can
    /// hand it mail on the results. Reported on evidence — that the pane has reached this server — rather than on
    /// capability, because a pane that never has may have no MCP surface at all (AC-156).
    /// </summary>
    [Fact]
    public async Task ListAgents_ReachableVia_ReportsPiggybackForATtyPaneThatCallsCockpitTools()
    {
        _DeskWith(deliversAtTurnStart: false, "pane-1", "pane-2");

        McpRequestContext.Set("pane-2");
        await _Tools().ListAgentsAsync();

        McpRequestContext.Set("pane-1");
        var seen = _Json(await _Tools().ListAgentsAsync())["agents"]!
            .AsArray()
            .First(agent => agent!["paneId"]!.GetValue<string>() == "pane-2")!;

        Assert.Equal("mcpPiggyback", seen["reachableVia"]!.GetValue<string>());
    }

    [Fact]
    public async Task ListAgents_ReachableVia_ReportsWakeForASilentPaneThatAgreedToBeWoken()
    {
        _DeskWith(deliversAtTurnStart: false, "pane-1", "pane-2");
        _coordinator.SetWakeConsent("pane-2", true);
        McpRequestContext.Set("pane-1");

        var seen = _Json(await _Tools().ListAgentsAsync())["agents"]!
            .AsArray()
            .First(agent => agent!["paneId"]!.GetValue<string>() == "pane-2")!;

        Assert.Equal("wake", seen["reachableVia"]!.GetValue<string>());
    }

    [Fact]
    public async Task ListAgents_ReachableVia_ReportsOperatorOnlyWhenNoRouteExists()
    {
        _DeskWith(deliversAtTurnStart: false, "pane-1", "pane-2");
        McpRequestContext.Set("pane-1");

        var seen = _Json(await _Tools().ListAgentsAsync())["agents"]!
            .AsArray()
            .First(agent => agent!["paneId"]!.GetValue<string>() == "pane-2")!;

        Assert.Equal("operatorOnly", seen["reachableVia"]!.GetValue<string>());
    }

    /// <summary>
    /// The warning and the reachability report are the same judgement, so they cannot disagree — a pane the host says
    /// it can reach must not also be one the send warns about, and that is only guaranteed by them sharing a source.
    /// </summary>
    [Fact]
    public async Task Notify_ToAPaneReachableByPiggyback_CarriesNoWarning()
    {
        _DeskWith(deliversAtTurnStart: false, "sender", "target");

        // The target calls a cockpit tool — that is all the piggyback needs.
        McpRequestContext.Set("target");
        await _Tools().ListAgentsAsync();

        var json = await _NotifyAs("sender", "target");

        Assert.Null(json["unreachable"]);
    }

    [Fact]
    public async Task ListAgents_APaneThatHasNeverCollectedMail_ReportsNoInboxRead()
    {
        _DeskWith(deliversAtTurnStart: false, "pane-1", "pane-2");
        McpRequestContext.Set("pane-1");

        var seen = _Json(await _Tools().ListAgentsAsync())["agents"]!
            .AsArray()
            .First(agent => agent!["paneId"]!.GetValue<string>() == "pane-2")!;

        Assert.Null(seen["lastInboxReadUtc"]);
    }

    [Fact]
    public async Task ListAgents_AfterAPaneCallsReadInbox_ReportsWhenItCollected()
    {
        _DeskWith(deliversAtTurnStart: false, "pane-1", "pane-2");
        var before = DateTimeOffset.UtcNow;

        McpRequestContext.Set("pane-2");
        _Tools().ReadInbox();

        McpRequestContext.Set("pane-1");
        var seen = _Json(await _Tools().ListAgentsAsync())["agents"]!
            .AsArray()
            .First(agent => agent!["paneId"]!.GetValue<string>() == "pane-2")!;

        Assert.True(seen["lastInboxReadUtc"]!.GetValue<DateTimeOffset>() >= before);
    }

    /// <summary>
    /// An empty read counts. The question the field answers is "is anybody collecting", and a pane that looked and
    /// found nothing is a pane that looks — which is exactly what a sender needs to know before it waits.
    /// </summary>
    [Fact]
    public async Task ListAgents_AReadThatFoundNothing_StillCountsAsCollecting()
    {
        _DeskWith(deliversAtTurnStart: false, "pane-1", "pane-2");

        McpRequestContext.Set("pane-2");
        var read = _Json(_Tools().ReadInbox());
        Assert.Equal(0, read["count"]!.GetValue<int>());

        McpRequestContext.Set("pane-1");
        var seen = _Json(await _Tools().ListAgentsAsync())["agents"]!
            .AsArray()
            .First(agent => agent!["paneId"]!.GetValue<string>() == "pane-2")!;

        Assert.NotNull(seen["lastInboxReadUtc"]);
    }

    /// <summary>The case from the field: delivered, and nothing is coming to collect it.</summary>
    [Fact]
    public async Task Notify_ToAPaneWithNoRouteAtAll_DeliversAndSaysNothingWillBringIt()
    {
        _DeskWith(deliversAtTurnStart: false, "sender", "silent");

        var json = await _NotifyAs("sender", "silent");

        Assert.True(json["ok"]!.GetValue<bool>());
        var warning = json["unreachable"]!.GetValue<string>();
        Assert.Contains("read_inbox", warning, StringComparison.Ordinal);
        Assert.Contains("silence", warning, StringComparison.Ordinal);
        // Delivered all the same — the warning is information for the sender, not a refusal on its behalf.
        Assert.Single(_inbox.Drain("silent", int.MaxValue).Messages);
    }

    /// <summary>
    /// A pane that left is not a wrong address, and telling a sender the two apart is the whole point: one means
    /// look the id up again, the other means the recipient is gone and this needs another route.
    /// </summary>
    [Fact]
    public async Task Notify_ToAPaneThatLeftTheDesk_SaysItLeftRatherThanThatItNeverExisted()
    {
        _DeskWith(deliversAtTurnStart: true, "sender", "leaver");
        // The pane was on the roster and its session ended — the host's own teardown call.
        _coordinator.Enroll("leaver");
        _coordinator.Forget("leaver");
        // ...and it is off the desk from here on.
        _DeskWith(deliversAtTurnStart: true, "sender");

        var json = await _NotifyAs("sender", "leaver");

        Assert.False(json["ok"]!.GetValue<bool>());
        var error = json["error"]!.GetValue<string>();
        Assert.Contains("has ended", error, StringComparison.Ordinal);
        Assert.Contains("the address was right", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Notify_ToAPaneIdTheCockpitHasNeverHeldAtAll_SaysSo()
    {
        _DeskWith(deliversAtTurnStart: true, "sender");

        var json = await _NotifyAs("sender", "pane-that-never-was");

        Assert.False(json["ok"]!.GetValue<bool>());
        var error = json["error"]!.GetValue<string>();
        Assert.Contains("no record of it ever having been one", error, StringComparison.Ordinal);
        Assert.DoesNotContain("has ended", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// Only a pane the roster actually held may be reported as departed. Forget is called on teardown paths that do
    /// not all check first, so without this a stray id would come back to a sender as "this pane was here and left"
    /// — a confident answer to a question the cockpit cannot answer.
    /// </summary>
    [Fact]
    public void Forget_OfAPaneTheRosterNeverHeld_DoesNotCountAsADeparture()
    {
        _coordinator.Forget("never-enrolled");

        Assert.Null(_coordinator.DepartedAtUtc("never-enrolled"));
    }

    /// <summary>The list of departures is bounded: it is a courtesy for a stale listing, not a history of the desk.</summary>
    [Fact]
    public void Departures_AreBounded_AndTheOldestFallOutFirst()
    {
        for (var i = 0; i < WorkspaceAgentCoordinator.MaxRememberedDepartures + 5; i++)
        {
            _coordinator.Enroll($"pane-{i}");
            _coordinator.Forget($"pane-{i}");
        }

        Assert.Null(_coordinator.DepartedAtUtc("pane-0"));
        Assert.NotNull(_coordinator.DepartedAtUtc($"pane-{WorkspaceAgentCoordinator.MaxRememberedDepartures + 4}"));
    }

    /// <summary>
    /// Turn-start delivery (AC-394) is collecting too, even though the pane called nothing — and it is confirmation
    /// that counts, not the taking. A batch that was taken and handed back was never read, and reporting it as a
    /// read would tell a sender someone is collecting when the send that would have carried it failed.
    /// </summary>
    [Fact]
    public void TurnStartDelivery_CountsAsAnInboxRead_OnlyOnceConfirmed()
    {
        var delivery = new AgentTurnInboxDelivery(_inbox, _coordinator);
        _inbox.Deliver("sender", "target", "heads-up", "the migration is running");

        var notice = delivery.TakeForTurn("target");
        Assert.NotNull(notice);
        Assert.Null(_coordinator.LastInboxReadUtc("target"));

        delivery.ReturnUndelivered(notice!);
        Assert.Null(_coordinator.LastInboxReadUtc("target"));

        var again = delivery.TakeForTurn("target");
        delivery.ConfirmDelivered(again!);
        Assert.NotNull(_coordinator.LastInboxReadUtc("target"));
    }
}
