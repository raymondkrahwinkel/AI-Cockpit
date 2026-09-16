using System.Text.Json.Nodes;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
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
/// split is taken only for a sender under a profile the pairing grant covers; any other sender's mail stays local.
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

    [Fact]
    public async Task Notify_WhileAControllerHoldsTheLine_ReachesTheControllersRead_NotTheLocalAssistant()
    {
        // The desk holds no local assistant at all: connected means there is one, and it is the controller's.
        McpRequestContext.Set(AgentOnTheNode);
        var reply = _Json(await _Agents().NotifyAsync(AssistantIdentity.PaneId, "done", "AC-795 tests are green."));

        Assert.True(reply["ok"]!.GetValue<bool>());
        Assert.Equal("DESKTOP", reply["controller"]!.GetValue<string>());
        Assert.Null(_inbox.PeekOldest(AssistantIdentity.PaneId));

        McpRequestContext.Set(NodeCallerIdentity.PaneId);
        var first = _Json(await _Node().ReadNodeInboxAsync(null));
        var message = Assert.Single(first["messages"]!.AsArray());
        Assert.Equal(AgentOnTheNode, message!["fromPaneId"]!.GetValue<string>());
        Assert.Equal("AC-795 tests are green.", message["body"]!.GetValue<string>());

        // The same read again, cursor unmoved — the poll that failed on the controller's side — hands it over
        // again; the cursor is what drops it.
        Assert.Single(_Json(await _Node().ReadNodeInboxAsync(null))["messages"]!.AsArray());
        Assert.Empty(_Json(await _Node().ReadNodeInboxAsync(message["id"]!.GetValue<string>()))["messages"]!.AsArray());
        Assert.Empty(_Json(await _Node().ReadNodeInboxAsync(null))["messages"]!.AsArray());
    }

    [Fact]
    public async Task ControllerDroppingAway_LandsWhatItHadNotCollected_WithTheLocalAssistant_SayingSo()
    {
        McpRequestContext.Set(AgentOnTheNode);
        await _Agents().NotifyAsync(AssistantIdentity.PaneId, "blocked", "Need a decision on the branch.");
        Assert.Null(_inbox.PeekOldest(AssistantIdentity.PaneId));

        _presence.Current = null;
        _presence.Raise();

        var landed = Assert.Single(_inbox.Drain(AssistantIdentity.PaneId, 25).Messages);
        Assert.Equal(AgentOnTheNode, landed.FromPaneId);
        Assert.Equal("blocked", landed.Kind);
        Assert.Equal("[Was meant for the controller of this machine, which dropped away before collecting it.] Need a decision on the branch.", landed.Body);
        McpRequestContext.Set(NodeCallerIdentity.PaneId);
        Assert.Empty(_Json(await _Node().ReadNodeInboxAsync(null))["messages"]!.AsArray());
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

    private AgentsMcpTools _Agents(bool profileShared = true, bool withLocalAssistant = false)
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
            pairing);
    }

    private NodeSessionMcpTools _Node() => new(
        Substitute.For<IAssistantReadGateway>(),
        Substitute.For<IAssistantAgentGateway>(),
        Substitute.For<INodePairingBroker>(),
        Substitute.For<ISessionProfileStore>(),
        new NodeDiscoveryId(Path.Combine(Path.GetTempPath(), $"node-discovery-id-{Guid.NewGuid():N}.txt")),
        _inbox);

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
