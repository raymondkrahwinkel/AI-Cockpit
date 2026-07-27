using System.Text.Json.Nodes;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Infrastructure.Agents;
using Cockpit.Infrastructure.Mcp;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Agents;

/// <summary>
/// The <c>list_agents</c> tool (AC-391): a session sees only the other agents on its own workspace, the workspace is
/// always the transport-verified caller's — never the agent-declared <c>session</c> — and a pane the workspace holds
/// but that never called in shows up as a visible gap rather than silently missing (AC-156).
/// </summary>
public sealed class AgentsMcpToolsTests : IDisposable
{
    private readonly IWorkspaceAgentGateway _gateway = Substitute.For<IWorkspaceAgentGateway>();
    private readonly WorkspaceAgentCoordinator _coordinator = new();

    private AgentsMcpTools _Tools() => new(_gateway, _coordinator);

    public void Dispose() => McpRequestContext.Set(null);

    [Fact]
    public void ListAgents_ReturnsThePanesOfTheCallersOwnWorkspace_WithNameProfileAndStatus()
    {
        var snapshot = new WorkspaceAgentSnapshot("ws-1", [
            new WorkspaceAgentPane("pane-1", "AC-13", "claude-code", "reviewing the diff"),
            new WorkspaceAgentPane("pane-2", "Session 2", "gpt-5", string.Empty),
        ]);
        _gateway.GetWorkspaceSnapshot("pane-1").Returns(snapshot);

        var json = JsonNode.Parse(_Tools().ListAgents("pane-1"));

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
    public void ListAgents_NeverSeesAnotherWorkspace_OnlyQueriesTheCallersOwnPane()
    {
        // Two workspaces, each with its own gateway snapshot. Only the caller's own pane id is ever asked for.
        var workspaceX = new WorkspaceAgentSnapshot("ws-x", [new WorkspaceAgentPane("pane-x", "X", null, string.Empty)]);
        var workspaceY = new WorkspaceAgentSnapshot("ws-y", [new WorkspaceAgentPane("pane-y", "Y", null, string.Empty)]);
        _gateway.GetWorkspaceSnapshot("pane-x").Returns(workspaceX);
        _gateway.GetWorkspaceSnapshot("pane-y").Returns(workspaceY);

        var json = JsonNode.Parse(_Tools().ListAgents("pane-x"));

        Assert.Equal("ws-x", json!["workspaceId"]!.GetValue<string>());
        var agents = json["agents"]!.AsArray();
        Assert.Single(agents);
        Assert.Equal("pane-x", agents[0]!["paneId"]!.GetValue<string>());
        // Never asked the gateway about the other workspace's pane at all.
        _gateway.DidNotReceive().GetWorkspaceSnapshot("pane-y");
    }

    [Fact]
    public void ListAgents_IsolationCannotBeBypassedByDeclaringAnotherWorkspacesSession()
    {
        // The request is transport-verified as pane-x (workspace X); the agent tries to see workspace Y by naming
        // pane-y as the `session` argument instead.
        var workspaceX = new WorkspaceAgentSnapshot("ws-x", [new WorkspaceAgentPane("pane-x", "X", null, string.Empty)]);
        var workspaceY = new WorkspaceAgentSnapshot("ws-y", [new WorkspaceAgentPane("pane-y", "Y", null, string.Empty)]);
        _gateway.GetWorkspaceSnapshot("pane-x").Returns(workspaceX);
        _gateway.GetWorkspaceSnapshot("pane-y").Returns(workspaceY);
        McpRequestContext.Set("pane-x");

        var json = JsonNode.Parse(_Tools().ListAgents("pane-y"));

        // Still workspace X — the verified pane wins over the declared session.
        Assert.Equal("ws-x", json!["workspaceId"]!.GetValue<string>());
        var agents = json["agents"]!.AsArray();
        Assert.Single(agents);
        Assert.Equal("pane-x", agents[0]!["paneId"]!.GetValue<string>());
        _gateway.DidNotReceive().GetWorkspaceSnapshot("pane-y");
    }

    [Fact]
    public void ListAgents_APaneThatNeverCalledIn_AppearsAsAVisibleGap_NotAsAbsence()
    {
        var snapshot = new WorkspaceAgentSnapshot("ws-1", [
            new WorkspaceAgentPane("pane-1", "Caller", null, string.Empty),
            new WorkspaceAgentPane("pane-2", "Silent", null, string.Empty),
        ]);
        _gateway.GetWorkspaceSnapshot("pane-1").Returns(snapshot);

        var json = JsonNode.Parse(_Tools().ListAgents("pane-1"));

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
    public void ListAgents_EnrollsTheVerifiedPane_NotTheAgentDeclaredSession()
    {
        var snapshot = new WorkspaceAgentSnapshot("ws-1", [
            new WorkspaceAgentPane("verified-pane", "Verified", null, string.Empty),
            new WorkspaceAgentPane("declared-pane", "Declared", null, string.Empty),
        ]);
        _gateway.GetWorkspaceSnapshot("verified-pane").Returns(snapshot);
        McpRequestContext.Set("verified-pane");

        _Tools().ListAgents("declared-pane");

        Assert.True(_coordinator.IsEnrolled("ws-1", "verified-pane"));
        Assert.False(_coordinator.IsEnrolled("ws-1", "declared-pane"));
    }

    [Fact]
    public void ListAgents_WithNoAttributablePane_Refuses()
    {
        _gateway.GetWorkspaceSnapshot(Arg.Any<string>()).Returns((WorkspaceAgentSnapshot?)null);

        var json = JsonNode.Parse(_Tools().ListAgents("ghost-pane"));

        Assert.False(json!["ok"]!.GetValue<bool>());
    }

    [Fact]
    public void ListAgents_ReservesEmptyPlaceholdersForClaimsAndWakeOptIn()
    {
        var snapshot = new WorkspaceAgentSnapshot("ws-1", [new WorkspaceAgentPane("pane-1", "Caller", null, string.Empty)]);
        _gateway.GetWorkspaceSnapshot("pane-1").Returns(snapshot);

        var json = JsonNode.Parse(_Tools().ListAgents("pane-1"));

        var self = json!["agents"]!.AsArray()[0]!;
        Assert.Empty(self["claims"]!.AsArray());
        Assert.Null(self["wakeOptIn"]);
    }
}
