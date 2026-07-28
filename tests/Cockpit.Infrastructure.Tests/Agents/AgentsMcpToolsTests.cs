using System.Text.Json.Nodes;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Infrastructure.Agents;
using Cockpit.Infrastructure.Mcp;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Agents;

/// <summary>
/// The <c>list_agents</c> tool (AC-391): identity comes only from the transport-verified caller
/// (<see cref="McpRequestContext.CurrentPaneId"/>) — the tool takes no session/pane argument at all, so there is
/// nothing an agent could declare to reach another workspace's roster — a request with no verified pane is
/// refused, and a pane the workspace holds but that never called in shows up as a visible gap rather than
/// silently missing (AC-156).
/// <para>
/// Every test that exercises a real caller sets <see cref="McpRequestContext"/> itself (never trusts a
/// substitute's default), so a guard-removal mutation that reads some other, unattributed value would fail these
/// rather than passing on an untested fallback path — <c>ListAgents_WithNoVerifiedPane_Refuses</c> is exactly the
/// case a fallback like that would have quietly allowed.
/// </para>
/// </summary>
public sealed class AgentsMcpToolsTests : IDisposable
{
    private readonly IWorkspaceAgentGateway _gateway = Substitute.For<IWorkspaceAgentGateway>();
    private readonly WorkspaceAgentCoordinator _coordinator = new();

    private AgentsMcpTools _Tools() => new(_gateway, _coordinator);

    public void Dispose() => McpRequestContext.Set(null);

    [Fact]
    public async Task ListAgents_WithNoVerifiedPane_Refuses()
    {
        // No McpRequestContext.Set at all — the shared-app-key path (McpAuthMiddleware sets null identity), and
        // what the in-process tool loop looks like before AC-89 issued it a per-session token. There is no
        // argument to fall back to reading instead: the tool takes none.
        McpRequestContext.Set(null);

        var json = JsonNode.Parse(await _Tools().ListAgentsAsync());

        Assert.False(json!["ok"]!.GetValue<bool>());
        _ = _gateway.DidNotReceiveWithAnyArgs().GetWorkspaceSnapshotAsync(default!);
    }

    [Fact]
    public async Task ListAgents_ReturnsThePanesOfTheCallersOwnWorkspace_WithNameProfileAndStatus()
    {
        var snapshot = new WorkspaceAgentSnapshot("ws-1", [
            new WorkspaceAgentPane("pane-1", "AC-13", "claude-code", "reviewing the diff"),
            new WorkspaceAgentPane("pane-2", "Session 2", "gpt-5", string.Empty),
        ]);
        _gateway.GetWorkspaceSnapshotAsync("pane-1").Returns(Task.FromResult<WorkspaceAgentSnapshot?>(snapshot));
        McpRequestContext.Set("pane-1");

        var json = JsonNode.Parse(await _Tools().ListAgentsAsync());

        Assert.True(json!["ok"]!.GetValue<bool>());
        Assert.Equal("ws-1", json["workspaceId"]!.GetValue<string>());
        var agents = json["agents"]!.AsArray();
        Assert.Equal(2, agents.Count);
        var first = agents.First(a => a!["paneId"]!.GetValue<string>() == "pane-1")!;
        Assert.Equal("AC-13", first["name"]!.GetValue<string>());
        Assert.Equal("claude-code", first["profile"]!.GetValue<string>());
        Assert.Equal("reviewing the diff", first["statusline"]!.GetValue<string>());
    }

    [Fact]
    public async Task ListAgents_NeverSeesAnotherWorkspace_OnlyQueriesTheVerifiedCallersOwnPane()
    {
        // Two workspaces, each with its own gateway snapshot. The transport verifies the caller as pane-x; only
        // that pane id may ever reach the gateway, whatever else might be true of the process (there is no
        // argument the tool could read a different one from — it takes none).
        var workspaceX = new WorkspaceAgentSnapshot("ws-x", [new WorkspaceAgentPane("pane-x", "X", null, string.Empty)]);
        var workspaceY = new WorkspaceAgentSnapshot("ws-y", [new WorkspaceAgentPane("pane-y", "Y", null, string.Empty)]);
        _gateway.GetWorkspaceSnapshotAsync("pane-x").Returns(Task.FromResult<WorkspaceAgentSnapshot?>(workspaceX));
        _gateway.GetWorkspaceSnapshotAsync("pane-y").Returns(Task.FromResult<WorkspaceAgentSnapshot?>(workspaceY));
        McpRequestContext.Set("pane-x");

        var json = JsonNode.Parse(await _Tools().ListAgentsAsync());

        Assert.Equal("ws-x", json!["workspaceId"]!.GetValue<string>());
        var agents = json["agents"]!.AsArray();
        Assert.Single(agents);
        Assert.Equal("pane-x", agents[0]!["paneId"]!.GetValue<string>());
        _ = _gateway.DidNotReceive().GetWorkspaceSnapshotAsync("pane-y");
    }

    [Fact]
    public async Task ListAgents_APaneThatNeverCalledIn_AppearsAsAVisibleGap_NotAsAbsence()
    {
        var snapshot = new WorkspaceAgentSnapshot("ws-1", [
            new WorkspaceAgentPane("pane-1", "Caller", null, string.Empty),
            new WorkspaceAgentPane("pane-2", "Silent", null, string.Empty),
        ]);
        _gateway.GetWorkspaceSnapshotAsync("pane-1").Returns(Task.FromResult<WorkspaceAgentSnapshot?>(snapshot));
        McpRequestContext.Set("pane-1");

        var json = JsonNode.Parse(await _Tools().ListAgentsAsync());

        var agents = json!["agents"]!.AsArray();
        // The caller enrolls itself just by calling.
        var self = agents.First(a => a!["paneId"]!.GetValue<string>() == "pane-1")!;
        Assert.True(self["enrolled"]!.GetValue<bool>());
        Assert.Null(self["gap"]);
        // The pane that never called in is still listed — as a gap, not omitted.
        var silent = agents.First(a => a!["paneId"]!.GetValue<string>() == "pane-2")!;
        Assert.False(silent["enrolled"]!.GetValue<bool>());
        Assert.False(string.IsNullOrEmpty(silent["gap"]!.GetValue<string>()));
    }

    [Fact]
    public async Task ListAgents_EnrollsTheVerifiedCaller()
    {
        var snapshot = new WorkspaceAgentSnapshot("ws-1", [
            new WorkspaceAgentPane("verified-pane", "Verified", null, string.Empty),
            new WorkspaceAgentPane("other-pane", "Other", null, string.Empty),
        ]);
        _gateway.GetWorkspaceSnapshotAsync("verified-pane").Returns(Task.FromResult<WorkspaceAgentSnapshot?>(snapshot));
        McpRequestContext.Set("verified-pane");

        await _Tools().ListAgentsAsync();

        Assert.True(_coordinator.IsEnrolled("verified-pane"));
        Assert.False(_coordinator.IsEnrolled("other-pane"));
    }

    [Fact]
    public async Task ListAgents_WithNoAttributableSession_Refuses()
    {
        _gateway.GetWorkspaceSnapshotAsync(Arg.Any<string>()).Returns(Task.FromResult<WorkspaceAgentSnapshot?>(null));
        McpRequestContext.Set("ghost-pane");

        var json = JsonNode.Parse(await _Tools().ListAgentsAsync());

        Assert.False(json!["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ListAgents_WhenTheGatewayThrows_ReturnsOkFalse_NotAProtocolError()
    {
        _gateway.GetWorkspaceSnapshotAsync(Arg.Any<string>()).Returns<WorkspaceAgentSnapshot?>(_ => throw new InvalidOperationException("boom"));
        McpRequestContext.Set("pane-1");

        var json = JsonNode.Parse(await _Tools().ListAgentsAsync());

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.False(string.IsNullOrEmpty(json["error"]!.GetValue<string>()));
    }

    [Fact]
    public async Task ListAgents_ReservesEmptyPlaceholdersForClaimsAndWakeOptIn()
    {
        var snapshot = new WorkspaceAgentSnapshot("ws-1", [new WorkspaceAgentPane("pane-1", "Caller", null, string.Empty)]);
        _gateway.GetWorkspaceSnapshotAsync("pane-1").Returns(Task.FromResult<WorkspaceAgentSnapshot?>(snapshot));
        McpRequestContext.Set("pane-1");

        var json = JsonNode.Parse(await _Tools().ListAgentsAsync());

        var self = json!["agents"]!.AsArray()[0]!;
        Assert.Empty(self["claims"]!.AsArray());
        Assert.Null(self["wakeOptIn"]);
    }
}
