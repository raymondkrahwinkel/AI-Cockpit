using System.Text.Json.Nodes;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Assistant;
using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Agents;
using Cockpit.Infrastructure.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Agents;

/// <summary>
/// AC-1322 at the node's end: a <c>notify cockpit-assistant</c> while a controller holds the line is queued for
/// that controller (criterion 1) and acknowledged by the cursor the controller sends back (criterion 2); what the
/// controller had not collected when it dropped away lands with the local assistant, saying so (criterion 3). The
/// split is taken only for a sender the holder may reach (AC-1405: its key's scope, or the pairing grant); any other
/// sender's mail stays local.
/// </summary>
public sealed class NodeNotifyRoutingTests : IDisposable
{
    private const string AgentOnTheNode = "pane-1";

    private readonly StubPresence _presence = new() { Current = new ActiveController("DESKTOP", DateTimeOffset.UtcNow) };

    private readonly AgentMessageInbox _inbox;

    private readonly string _auditPath = Path.Combine(Path.GetTempPath(), $"agent-notify-audit-{Guid.NewGuid():N}.jsonl");

    public NodeNotifyRoutingTests()
    {
        _inbox = new AgentMessageInbox(_presence);
    }

    // The scope the pairing grant draws (AC-1292) is the scope of this split too: the same sender, on a desk that does
    // hold a local assistant, reaches the controller under a shared profile and only the local inbox under any other.
    [Theory]
    [InlineData(true, "DESKTOP", 0)]
    [InlineData(false, null, 1)]
    public async Task Notify_ReachesTheController_OnlyFromAProfileTheGrantCovers(bool profileShared, string? controller, int keptLocally)
    {
        McpRequestContext.Set(AgentOnTheNode);
        var reply = _Json(await _Agents(profileShared, withLocalAssistant: true).NotifyAsync(AssistantIdentity.PaneId, "done", "Green."));

        Assert.True(reply["ok"]!.GetValue<bool>());
        Assert.Equal(controller, reply["controller"]?.GetValue<string>());
        Assert.Equal(keptLocally, _inbox.Drain(AssistantIdentity.PaneId, 25).Messages.Count);
        McpRequestContext.Set(NodeCallerIdentity.PaneId);
        Assert.Equal(1 - keptLocally, _Json(await _Node().ReadNodeInboxAsync(null))["messages"]!.AsArray().Count);
    }

    // AC-1405: a controller holding the line by connect key gets the mail of a session its key's scope reaches, under
    // a profile no pairing covers, and not of one in a project outside that scope.
    [Theory]
    [InlineData(false, "project-a", "project-a", "DESKTOP", 0)]
    [InlineData(false, "project-a", "project-b", null, 1)]
    [InlineData(true, "project-a", null, "DESKTOP", 0)]
    public async Task Notify_ReachesAKeyController_OnlyFromASessionTheKeyScopeReaches(bool keyOnEveryProject, string keyProject, string? sessionProject, string? controller, int keptLocally)
    {
        _presence.Current = new ActiveController("DESKTOP", DateTimeOffset.UtcNow, "ck_test", new ConnectKeyScope
        {
            AllowAllProjects = keyOnEveryProject,
            AllowedProjectIds = [keyProject],
        });
        var session = Substitute.For<ISessionHandle>();
        session.ProjectId.Returns(sessionProject);
        var sessions = Substitute.For<ISessionRegistry>();
        sessions.Find(AgentOnTheNode).Returns(session);
        McpRequestContext.Set(AgentOnTheNode);
        var reply = _Json(await _Agents(profileShared: false, withLocalAssistant: true, sessions).NotifyAsync(AssistantIdentity.PaneId, "done", "Green."));

        Assert.True(reply["ok"]!.GetValue<bool>());
        Assert.Equal(controller, reply["controller"]?.GetValue<string>());
        Assert.Equal(keptLocally, _inbox.Drain(AssistantIdentity.PaneId, 25).Messages.Count);
        McpRequestContext.Set(NodeCallerIdentity.PaneId);
        Assert.Equal(1 - keptLocally, _Json(await _Node().ReadNodeInboxAsync(null))["messages"]!.AsArray().Count);
    }

    private AgentsMcpTools _Agents(bool profileShared = true, bool withLocalAssistant = false, ISessionRegistry? sessions = null)
    {
        var gateway = Substitute.For<IWorkspaceAgentGateway>();
        var assistant = new WorkspaceAgentPane(AssistantIdentity.PaneId, "Assistant", "personal", "", true);
        gateway.GetWorkspaceSnapshotAsync(AgentOnTheNode).Returns(Task.FromResult<WorkspaceAgentSnapshot?>(
            new WorkspaceAgentSnapshot("ws-1", [
                new WorkspaceAgentPane(AgentOnTheNode, AgentOnTheNode, "personal", "", true),
                .. withLocalAssistant ? new[] { assistant } : [],
            ])));
        var pairing = Substitute.For<INodePairingBroker>();
        pairing.IsProfileAllowed("personal").Returns(profileShared);
        return new AgentsMcpTools(
            gateway,
            new WorkspaceAgentCoordinator(),
            _inbox,
            new AgentNotifyAuditLog(_auditPath, NullLogger<AgentNotifyAuditLog>.Instance),
            new AgentResourceClaims(),
            new AgentLineBudget(TimeProvider.System, TimeSpan.FromMinutes(1), 10_000, 10_000),
            _presence,
            pairing,
            sessions);
    }

    private NodeSessionMcpTools _Node() => new(
        Substitute.For<IAssistantReadGateway>(),
        Substitute.For<IAssistantAgentGateway>(),
        Substitute.For<INodePairingBroker>(),
        Substitute.For<ISessionProfileStore>(),
        new NodeDiscoveryId(Path.Combine(Path.GetTempPath(), $"node-discovery-id-{Guid.NewGuid():N}.txt")),
        _inbox,
        Substitute.For<IAssistantMemory>());

    private static JsonNode _Json(string result) => JsonNode.Parse(result)!;

    public void Dispose()
    {
        McpRequestContext.Set(null);
        if (File.Exists(_auditPath))
        {
            File.Delete(_auditPath);
        }
    }

    private sealed class StubPresence : INodeControllerPresence
    {
        public ActiveController? Current { get; set; }

        public event EventHandler? Changed;

        public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
    }
}
