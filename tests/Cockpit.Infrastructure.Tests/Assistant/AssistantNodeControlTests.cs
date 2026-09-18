using System.Text.Json.Nodes;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Assistant;
using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Agents;
using Cockpit.Infrastructure.Assistant;
using Cockpit.Infrastructure.Consent;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Tests.Mcp;
using Cockpit.Plugins.Abstractions.Consent;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Assistant;

// AC-1323: the controller's assistant runs sessions on a paired node through the same tools it runs local ones
// with, addressed by "<node> · <paneId>"; the node bounds every call by the pairing's scope.
public sealed class AssistantNodeControlTests : IDisposable
{
    private const string Node = "LAPTOP";

    private const string NodePane = "0123456789abcdef0123456789abcdef";

    private static readonly string Address = NodeSessionAddress.For(Node, NodePane);

    private readonly IAssistantAgentGateway _gateway = Substitute.For<IAssistantAgentGateway>();

    private readonly IAssistantReadGateway _read = Substitute.For<IAssistantReadGateway>();

    private readonly INodeSessionsClient _nodes = Substitute.For<INodeSessionsClient>();

    private readonly IConsentBroker _consent = Substitute.For<IConsentBroker>();

    public AssistantNodeControlTests()
    {
        McpRequestContext.Set(AssistantIdentity.PaneId);
        _consent.RequestConsentAsync(Arg.Any<ConsentRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ConsentDecision(ConsentOutcome.Approved));
    }

    private AssistantAgentMcpTools _ActTools() => new(_gateway, Substitute.For<IAssistantMemory>(), _consent, _nodes);

    private AssistantReadMcpTools _ReadTools() => new(
        _read,
        Substitute.For<IDelegationService>(),
        _nodes,
        new NodeDiscoveryId(Path.Combine(Path.GetTempPath(), $"node-discovery-id-{Guid.NewGuid():N}.txt")));

    private static JsonNode _Json(string result) => JsonNode.Parse(result)!;

    // Criterion 1: a start with `node` lands on the node, in the project and profile the scope allows, and comes
    // back under the node address; a project outside the scope is refused with the node's reason, and nothing
    // starts anywhere.
    [Fact]
    public async Task StartAgent_WithANode_StartsThere_AndRelaysTheScopeRefusal()
    {
        _nodes.StartAsync(Node, "Laptop Sonnet", "project-allowed", "run the tests", "AC-1 tests", Arg.Any<CancellationToken>())
            .Returns(new NodeStartResult(null, NodePane, "AC-1 tests", "Laptop Sonnet", true));

        var started = _Json(await _ActTools().StartAgentAsync(
            profile: "Laptop Sonnet", projectId: "project-allowed", prompt: "run the tests", name: "AC-1 tests", node: Node));

        Assert.True((bool)started["ok"]!);
        Assert.Equal(Address, (string)started["paneId"]!);
        Assert.Equal("Laptop Sonnet", (string)started["resolvedProfile"]!);
        Assert.Equal(Node, (string)started["machine"]!["name"]!);
        Assert.False((bool)started["machine"]!["local"]!);

        _nodes.StartAsync(Node, "Laptop Sonnet", "project-private", null, null, Arg.Any<CancellationToken>())
            .Returns(new NodeStartResult("This node's operator has not allowed the project 'project-private'."));

        var refused = _Json(await _ActTools().StartAgentAsync(profile: "Laptop Sonnet", projectId: "project-private", node: Node));

        Assert.False((bool)refused["ok"]!);
        Assert.Contains("not allowed the project 'project-private'", (string)refused["error"]!);
        Assert.Contains(Node, (string)refused["error"]!);
        await _gateway.DidNotReceiveWithAnyArgs().SpawnAsync(default!);
    }

    // Criterion 2, both ends of the line: each tool on a node address reaches the node with that machine's own
    // pane id, an unreachable node names the machine and the local gateway stays untouched; on the node, the
    // same act reaches the gateway under an allowed profile and is refused before it under one that is not.
    [Theory]
    [InlineData("stop_agent")]
    [InlineData("send_prompt")]
    [InlineData("send_message")]
    [InlineData("rename_session")]
    [InlineData("read_transcript")]
    public async Task ATool_OnANodeAddress_ActsOnTheNode_AndAnUnreachableNodeNamesTheMachine(string tool)
    {
        var route = _Routes(tool);

        route.NodeAnswers(null);
        var reply = _Json(await route.CallHere());
        Assert.True((bool)reply["ok"]!, reply.ToJsonString());
        Assert.Equal(Node, (string)reply["machine"]!["name"]!);
        Assert.False((bool)reply["machine"]!["local"]!);
        await route.CalledTheNode();

        route.NodeAnswers($"{Node} did not answer within 10s.");
        var down = _Json(await route.CallHere());
        Assert.False((bool)down["ok"]!);
        Assert.Contains(Node, (string)down["error"]!);
        Assert.Empty(_gateway.ReceivedCalls());
        Assert.Empty(_read.ReceivedCalls());

        // The node's end: the same act, bounded by the pairing's scope.
        McpRequestContext.Set(NodeCallerIdentity.PaneId);
        var pairing = new NodeSessionMcpToolsTests.StubPairing { Profiles = { "Laptop Sonnet" } };
        var nodeRead = Substitute.For<IAssistantReadGateway>();
        nodeRead.ListSessionsAsync().Returns([new AssistantSessionRow(NodePane, "AC-1", "Laptop Sonnet", "", "ws", "Sessions")]);
        var nodeGateway = Substitute.For<IAssistantAgentGateway>();
        route.PrimeTheNode(nodeGateway, nodeRead);
        var nodeTools = new NodeSessionMcpTools(
            nodeRead, nodeGateway, pairing, new NodeSessionMcpToolsTests.StubProfileStore(),
            new NodeDiscoveryId(Path.Combine(Path.GetTempPath(), $"node-discovery-id-{Guid.NewGuid():N}.txt")), new AgentMessageInbox(),
            new NodeSessionMcpToolsTests.StubMemory());

        var onNode = _Json(await route.CallOnNode(nodeTools));
        Assert.True((bool)onNode["ok"]!, onNode.ToJsonString());
        Assert.Equal(Environment.MachineName, (string)onNode["node"]!);
        await route.ReachedTheGateway(nodeGateway, nodeRead);

        pairing.Profiles.Clear();
        nodeGateway.ClearReceivedCalls();
        nodeRead.ClearReceivedCalls();
        var outsideScope = _Json(await route.CallOnNode(nodeTools));
        Assert.False((bool)outsideScope["ok"]!);
        Assert.Contains("allowed", (string)outsideScope["error"]!);
        Assert.Empty(nodeGateway.ReceivedCalls());
        Assert.DoesNotContain(nodeRead.ReceivedCalls(), call => call.GetMethodInfo().Name != nameof(IAssistantReadGateway.ListSessionsAsync));
    }

    // AC-1326 criterion 3 plus its counterproofs as rows: no `node` refuses when the last known node snapshot
    // also has the project (read off INodeSessionsClient, never a fresh call); node: "local" bypasses that on
    // purpose, and an unknown-to-the-snapshot project starts locally exactly as it does today.
    [Theory]
    [InlineData(null, "shared-id", false, 0, "exists on this machine and on LAPTOP")]
    [InlineData("local", "shared-id", true, 1, "\"ok\":true")]
    [InlineData(null, "local-only-id", true, 1, "\"ok\":true")]
    public async Task StartAgent_WithoutNode_RefusesOnlyWhenTheLastNodeSnapshotAlsoKnowsTheProject(
        string? node, string projectId, bool expectOk, int expectedSpawnCalls, string expectedFragment)
    {
        _nodes.ListNodesAsync(Arg.Any<CancellationToken>()).Returns([Node]);
        _nodes.TryGetLastSnapshot(Node).Returns((
            new NodeSessionsSnapshot(Node, [], [], [new NodeProjectRow("shared-id", "Shared")]),
            DateTimeOffset.UtcNow));
        _gateway.SpawnAsync(Arg.Any<AgentSpawnRequest>(), Arg.Any<CancellationToken>())
            .Returns(AgentSpawnResult.Started("pane-1", "AC-1", "/repo"));

        var reply = _Json(await _ActTools().StartAgentAsync(workspaceId: "ws-1", profile: "Sonnet", projectId: projectId, node: node));

        Assert.Equal(expectOk, (bool)reply["ok"]!);
        Assert.Contains(expectedFragment, reply.ToJsonString());
        await _gateway.Received(expectedSpawnCalls).SpawnAsync(Arg.Any<AgentSpawnRequest>(), Arg.Any<CancellationToken>());
        await _nodes.DidNotReceiveWithAnyArgs().StartAsync(default!, default!);
    }

    // Criterion 3: a watch on a node address is refused with a reason that says what does work there.
    [Fact]
    public async Task WatchSession_OnANodeAddress_Refuses_AndSaysWhatWorksInstead()
    {
        var reply = _Json(await _ActTools().WatchSessionAsync(Address, ["busy-to-idle"]));

        Assert.False((bool)reply["ok"]!);
        var reason = (string)reply["error"]!;
        Assert.Contains(Node, reason);
        Assert.Contains("read_transcript", reason);
        Assert.Contains("send_prompt", reason);
        Assert.Contains("notify cockpit-assistant", reason);
        await _gateway.DidNotReceiveWithAnyArgs().WatchSessionAsync(default!, default!);
        Assert.Empty(_nodes.ReceivedCalls());
    }

    // AC-1329, criteria 1+2: "behaviour" reaches this machine and every paired node, waited out in full, an
    // unreachable one reported as not delivered rather than queued, and refuses `machines` outright; "machine"
    // reaches only the machines named and refuses without any, never touching `nodes` in either refusal.
    [Theory]
    [InlineData("behaviour", null, true, "not delivered to PHONE", 1, 1, 1)]
    [InlineData("behaviour", new[] { "LAPTOP" }, false, "machines is refused for scope", 0, 0, 0)]
    [InlineData("machine", new[] { "LAPTOP" }, true, "\"machine\":\"LAPTOP\"", 0, 1, 0)]
    [InlineData("machine", null, false, "which machine does this fact hold on", 0, 0, 0)]
    public async Task Remember_RoutesToTheScopesDestinations_AndWaitsForEachBeforeAnswering(
        string scope, string[]? machines, bool expectOk, string expectedFragment,
        int expectLocalWrites, int expectReachableWrites, int expectUnreachableWrites)
    {
        const string ReachableNode = "LAPTOP";
        const string UnreachableNode = "PHONE";
        var memory = Substitute.For<IAssistantMemory>();
        _nodes.ListNodesAsync(Arg.Any<CancellationToken>()).Returns([ReachableNode, UnreachableNode]);
        _nodes.RememberOnNodeAsync(ReachableNode, "remember this", Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        _nodes.RememberOnNodeAsync(UnreachableNode, "remember this", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns($"{UnreachableNode} did not answer within 10s.");

        var tools = new AssistantAgentMcpTools(_gateway, memory, _consent, _nodes);
        var reply = _Json(await tools.RememberAsync("remember this", scope, machines));

        Assert.Equal(expectOk, (bool)reply["ok"]!);
        Assert.Contains(expectedFragment, reply.ToJsonString());
        await memory.Received(expectLocalWrites).RememberAsync("remember this", Arg.Any<AssistantMemoryScope>(), Arg.Any<CancellationToken>());
        await _nodes.Received(expectReachableWrites).RememberOnNodeAsync(ReachableNode, "remember this", Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _nodes.Received(expectUnreachableWrites).RememberOnNodeAsync(UnreachableNode, "remember this", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private sealed record _Route(
        Func<Task<string>> CallHere,
        Action<string?> NodeAnswers,
        Func<Task> CalledTheNode,
        Func<NodeSessionMcpTools, Task<string>> CallOnNode,
        Action<IAssistantAgentGateway, IAssistantReadGateway> PrimeTheNode,
        Func<IAssistantAgentGateway, IAssistantReadGateway, Task> ReachedTheGateway);

    private _Route _Routes(string tool) => tool switch
    {
        "stop_agent" => new(
            () => _ActTools().StopAgentAsync(Address),
            error => _nodes.StopAsync(Node, NodePane, Arg.Any<CancellationToken>()).Returns(error),
            () => _nodes.Received().StopAsync(Node, NodePane, Arg.Any<CancellationToken>()),
            tools => tools.StopNodeAgentAsync(NodePane),
            (gateway, _) => gateway.StopAsync(NodePane, SpawnCaller.Controller, NodeCallerIdentity.PaneId, Arg.Any<CancellationToken>()).Returns(AgentStopResult.Stopped(NodePane, "AC-1")),
            (gateway, _) => gateway.Received().StopAsync(NodePane, SpawnCaller.Controller, NodeCallerIdentity.PaneId, Arg.Any<CancellationToken>())),
        "send_prompt" => new(
            () => _ActTools().SendPromptAsync(Address, "run the tests"),
            error => _nodes.SendPromptAsync(Node, NodePane, "run the tests", Arg.Any<CancellationToken>()).Returns(error),
            () => _nodes.Received().SendPromptAsync(Node, NodePane, "run the tests", Arg.Any<CancellationToken>()),
            tools => tools.SendNodePromptAsync(NodePane, "run the tests"),
            (gateway, _) => gateway.SendPromptAsync(NodePane, "run the tests", Arg.Any<CancellationToken>()).Returns(AgentPromptResult.Handed(NodePane, "AC-1", true)),
            (gateway, _) => gateway.Received().SendPromptAsync(NodePane, "run the tests", Arg.Any<CancellationToken>())),
        "send_message" => new(
            () => _ActTools().SendMessageAsync(Address, "heads-up", "the branch moved"),
            error => _nodes.SendMessageAsync(Node, NodePane, "heads-up", "the branch moved", Arg.Any<CancellationToken>()).Returns(error),
            () => _nodes.Received().SendMessageAsync(Node, NodePane, "heads-up", "the branch moved", Arg.Any<CancellationToken>()),
            tools => tools.SendNodeMessageAsync(NodePane, "heads-up", "the branch moved"),
            (gateway, _) => gateway.SendMessageAsync(NodePane, "heads-up", "the branch moved", Arg.Any<CancellationToken>()).Returns(AgentMessageResult.Sent(NodePane, "AC-1", "m1", false, true)),
            (gateway, _) => gateway.Received().SendMessageAsync(NodePane, "heads-up", "the branch moved", Arg.Any<CancellationToken>())),
        "rename_session" => new(
            () => _ActTools().RenameSessionAsync(Address, "AC-1 tests"),
            error => _nodes.RenameAsync(Node, NodePane, "AC-1 tests", Arg.Any<CancellationToken>()).Returns(error),
            () => _nodes.Received().RenameAsync(Node, NodePane, "AC-1 tests", Arg.Any<CancellationToken>()),
            tools => tools.RenameNodeSessionAsync(NodePane, "AC-1 tests"),
            (gateway, _) => gateway.RenameSessionAsync(NodePane, "AC-1 tests", Arg.Any<CancellationToken>()).Returns(AssistantRenameResult.Renamed("AC-1 tests")),
            (gateway, _) => gateway.Received().RenameSessionAsync(NodePane, "AC-1 tests", Arg.Any<CancellationToken>())),
        "read_transcript" => new(
            () => _ReadTools().ReadTranscriptAsync(Address, 5),
            error => _nodes.ReadTranscriptAsync(Node, NodePane, 5, Arg.Any<CancellationToken>()).Returns(error is null
                ? new NodeTranscriptRead(new AssistantTranscript(NodePane, "AC-1", 1, [new AssistantTranscriptEntry("UserText", "hello", null)]))
                : new NodeTranscriptRead(null, error)),
            () => _nodes.Received().ReadTranscriptAsync(Node, NodePane, 5, Arg.Any<CancellationToken>()),
            tools => tools.ReadNodeTranscriptAsync(NodePane, 5),
            (_, read) => read.ReadTranscriptAsync(NodePane, 5).Returns(new AssistantTranscript(NodePane, "AC-1", 1, [new AssistantTranscriptEntry("UserText", "hello", null)])),
            (_, read) => read.Received().ReadTranscriptAsync(NodePane, 5)),
        _ => throw new ArgumentOutOfRangeException(nameof(tool)),
    };

    public void Dispose() => McpRequestContext.Set(null);
}
