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

    private readonly Clock _clock = new();

    private readonly NodeControllerPresence _presence;

    private readonly INodePairingBroker _pairing = Substitute.For<INodePairingBroker>();

    private readonly AgentMessageInbox _inbox;

    private readonly string _auditPath = Path.Combine(Path.GetTempPath(), $"agent-notify-audit-{Guid.NewGuid():N}.jsonl");

    public NodeNotifyRoutingTests()
    {
        _presence = new NodeControllerPresence(_clock);
        _inbox = new AgentMessageInbox(_presence);
    }

    // The scope the pairing grant draws (AC-1292) is the scope of this split too: the same sender, on a desk that does
    // hold a local assistant, reaches the controller under a shared profile and only the local inbox under any other.
    [Theory]
    [InlineData(true, "DESKTOP", 0)]
    [InlineData(false, null, 1)]
    public async Task Notify_ReachesTheController_OnlyFromAProfileTheGrantCovers(bool profileShared, string? controller, int keptLocally)
    {
        _presence.Seen("DESKTOP");
        McpRequestContext.Set(AgentOnTheNode);
        var reply = _Json(await _Agents(profileShared, withLocalAssistant: true).NotifyAsync(AssistantIdentity.PaneId, "done", "Green."));

        Assert.True(reply["ok"]!.GetValue<bool>());
        Assert.Equal(controller, reply["controller"]?.GetValue<string>());
        Assert.Equal(keptLocally, _inbox.Drain(AssistantIdentity.PaneId, 25).Messages.Count);
        McpRequestContext.Set(NodeCallerIdentity.PaneId);
        Assert.Equal(1 - keptLocally, _Json(await _Node().ReadNodeInboxAsync(null))["messages"]!.AsArray().Count);
    }

    // AC-1405: a key holding the line (on project A, or every project) gets the mail of a session its scope reaches,
    // under a profile no pairing covers. What it reads is held to the reader's scope at the time of reading: another
    // key reads nothing, a key taking over after the first one's ran out misses its backlog, and so does a narrowed key.
    [Theory]
    [InlineData(false, "project-a", "project-a", "DESKTOP", 0, "ck_test", false, "project-a", 0, 1)]
    [InlineData(false, "project-a", "project-b", null, 1, "ck_test", false, "project-a", 0, 0)]
    [InlineData(true, "project-a", null, "DESKTOP", 0, "ck_test", true, "project-a", 0, 1)]
    [InlineData(false, "project-a", "project-a", "DESKTOP", 0, "ck_other", false, "project-a", 0, null)]
    [InlineData(false, "project-a", "project-a", "DESKTOP", 0, "ck_other", false, "project-b", 60, 0)]
    [InlineData(true, "project-a", "project-b", "DESKTOP", 0, "ck_test", false, "project-a", 0, 0)]
    public async Task Notify_ReachesAKeyController_OnlyFromASessionTheKeyScopeReaches(
        bool holderOnEveryProject, string holderProject, string? sessionProject, string? controller, int keptLocally,
        string readerKey, bool readerOnEveryProject, string readerProject, int secondsLater, int? collected)
    {
        _presence.Seen("DESKTOP", _Key("ck_test", holderOnEveryProject, holderProject));
        var session = Substitute.For<ISessionHandle>();
        session.ProjectId.Returns(sessionProject);
        var sessions = Substitute.For<ISessionRegistry>();
        sessions.Find(AgentOnTheNode).Returns(session);
        McpRequestContext.Set(AgentOnTheNode);
        var reply = _Json(await _Agents(profileShared: false, withLocalAssistant: true, sessions).NotifyAsync(AssistantIdentity.PaneId, "done", "Green."));

        Assert.True(reply["ok"]!.GetValue<bool>());
        Assert.Equal(controller, reply["controller"]?.GetValue<string>());
        Assert.Equal(keptLocally, _inbox.Drain(AssistantIdentity.PaneId, 25).Messages.Count);
        _clock.Now += TimeSpan.FromSeconds(secondsLater);
        var reader = _Key(readerKey, readerOnEveryProject, readerProject);
        _presence.Seen(readerKey, reader);
        McpRequestContext.Set(NodeCallerIdentity.PaneId, reader);
        Assert.Equal(collected, _Json(await _Node().ReadNodeInboxAsync(null))["messages"]?.AsArray().Count);
    }

    // A holding connect key as the door stamps it, running out half a minute from now.
    private NodeCaller _Key(string prefix, bool onEveryProject, string project) => new(
        prefix,
        prefix,
        ConnectKeyCapability.Operate,
        "",
        CancellationToken.None,
        HoldsAssistant: true,
        new ConnectKeyScope { AllowAllProjects = onEveryProject, AllowedProjectIds = [project] },
        _clock.Now.AddSeconds(30));

    private AgentsMcpTools _Agents(bool profileShared = true, bool withLocalAssistant = false, ISessionRegistry? sessions = null)
    {
        var gateway = Substitute.For<IWorkspaceAgentGateway>();
        var assistant = new WorkspaceAgentPane(AssistantIdentity.PaneId, "Assistant", "personal", "", true);
        gateway.GetWorkspaceSnapshotAsync(AgentOnTheNode).Returns(Task.FromResult<WorkspaceAgentSnapshot?>(
            new WorkspaceAgentSnapshot("ws-1", [
                new WorkspaceAgentPane(AgentOnTheNode, AgentOnTheNode, "personal", "", true),
                .. withLocalAssistant ? new[] { assistant } : [],
            ])));
        _pairing.IsProfileAllowed("personal").Returns(profileShared);
        return new AgentsMcpTools(
            gateway,
            new WorkspaceAgentCoordinator(),
            _inbox,
            new AgentNotifyAuditLog(_auditPath, NullLogger<AgentNotifyAuditLog>.Instance),
            new AgentResourceClaims(),
            new AgentLineBudget(TimeProvider.System, TimeSpan.FromMinutes(1), 10_000, 10_000),
            _presence,
            _pairing,
            sessions);
    }

    private NodeSessionMcpTools _Node() => new(
        Substitute.For<IAssistantReadGateway>(),
        Substitute.For<IAssistantAgentGateway>(),
        _pairing,
        Substitute.For<ISessionProfileStore>(),
        new NodeDiscoveryId(Path.Combine(Path.GetTempPath(), $"node-discovery-id-{Guid.NewGuid():N}.txt")),
        _inbox,
        Substitute.For<IAssistantMemory>(),
        presence: _presence);

    private static JsonNode _Json(string result) => JsonNode.Parse(result)!;

    public void Dispose()
    {
        McpRequestContext.Set(null);
        if (File.Exists(_auditPath))
        {
            File.Delete(_auditPath);
        }
    }

    // A clock the test moves by hand; the presence's window timer never fires, so only a key running out ends a hold.
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => Substitute.For<ITimer>();
    }
}
