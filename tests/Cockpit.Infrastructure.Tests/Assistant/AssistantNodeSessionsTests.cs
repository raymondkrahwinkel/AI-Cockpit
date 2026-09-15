using System.Diagnostics;
using System.Text.Json.Nodes;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Assistant;
using Cockpit.Infrastructure.Assistant;
using Cockpit.Infrastructure.Mcp;
using NSubstitute;
using Xunit.Abstractions;

namespace Cockpit.Infrastructure.Tests.Assistant;

// AC-1320: the assistant reads the sessions of a paired node through list_sessions, every row stamped with the
// machine it runs on, and the tools that act on a pane refuse a node's session before touching anything.
public sealed class AssistantNodeSessionsTests(ITestOutputHelper output) : IDisposable
{
    private const string Node = "LAPTOP";

    // The same raw pane id on both machines — the case a machine-less list cannot tell apart.
    private const string SharedPaneId = "0123456789abcdef0123456789abcdef";

    private readonly IAssistantReadGateway _read = Substitute.For<IAssistantReadGateway>();

    private readonly INodeSessionsClient _nodes = Substitute.For<INodeSessionsClient>();

    private readonly NodeDiscoveryId _self = new(Path.Combine(Path.GetTempPath(), $"node-discovery-id-{Guid.NewGuid():N}.txt"));

    private AssistantReadMcpTools _ReadTools() => new(_read, Substitute.For<IDelegationService>(), _nodes, _self);

    private static JsonNode _Json(string result) => JsonNode.Parse(result)!;

    private static AssistantSessionRow _Local(string paneId, string name) => new(paneId, name, "default", "", "ws", "Sessions");

    [Fact]
    public async Task ListSessions_WithAReachableNode_ListsBothMachines_AndTellsTheSamePaneIdApart()
    {
        McpRequestContext.Set(AssistantIdentity.PaneId);
        _read.ListSessionsAsync().Returns([_Local(SharedPaneId, "AC-1")]);
        _nodes.ListNodesAsync(Arg.Any<CancellationToken>()).Returns([Node]);
        _nodes.ReadAsync(Node, Arg.Any<CancellationToken>()).Returns(new NodeSessionsSnapshot(
            Node,
            [new NodeSessionRow(SharedPaneId, "AC-1", "default", "building", "Busy")],
            [],
            [],
            DiscoveryId: "NODE-ID"));

        var reply = _Json(await _ReadTools().ListSessionsAsync());

        Assert.True((bool)reply["ok"]!);
        var rows = reply["sessions"]!.AsArray();
        Assert.Equal(2, rows.Count);

        var local = rows[0]!;
        Assert.Equal(SharedPaneId, (string)local["paneId"]!);
        Assert.True((bool)local["machine"]!["local"]!);
        Assert.Equal(Environment.MachineName, (string)local["machine"]!["name"]!);
        Assert.Equal(_self.Value, (string)local["machine"]!["discoveryId"]!);

        var remote = rows[1]!;
        Assert.Equal($"{Node} · {SharedPaneId}", (string)remote["paneId"]!);
        Assert.False((bool)remote["machine"]!["local"]!);
        Assert.Equal(Node, (string)remote["machine"]!["name"]!);
        Assert.Equal("NODE-ID", (string)remote["machine"]!["discoveryId"]!);
        Assert.Equal("Busy", (string)remote["status"]!);

        Assert.NotEqual((string)local["paneId"]!, (string)remote["paneId"]!);
        var node = Assert.Single(reply["nodes"]!.AsArray());
        Assert.True((bool)node!["reachable"]!);
        Assert.Equal(1, (int)node["sessionCount"]!);
    }

    [Fact]
    public async Task ListSessions_WithAnUnreachableNode_KeepsTheLocalListComplete_AndRemembersSinceWhen()
    {
        McpRequestContext.Set(AssistantIdentity.PaneId);
        _read.ListSessionsAsync().Returns([_Local("a1", "AC-1"), _Local("a2", "AC-2")]);
        _nodes.ListNodesAsync(Arg.Any<CancellationToken>()).Returns([Node]);
        // A node that is off does not answer at all — the read hangs until the caller's budget cancels it.
        _nodes.ReadAsync(Node, Arg.Any<CancellationToken>()).Returns(async call =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>());
            return new NodeSessionsSnapshot(Node, [], [], []);
        });
        var tools = _ReadTools();

        var clock = Stopwatch.StartNew();
        var first = _Json(await tools.ListSessionsAsync());
        var firstCall = clock.Elapsed;

        clock.Restart();
        var second = _Json(await tools.ListSessionsAsync());
        var secondCall = clock.Elapsed;
        output.WriteLine($"list_sessions with an unreachable node: first call {firstCall.TotalMilliseconds:0} ms, next call {secondCall.TotalMilliseconds:0} ms");

        Assert.Equal(2, first["sessions"]!.AsArray().Count);
        var node = Assert.Single(first["nodes"]!.AsArray());
        Assert.False((bool)node!["reachable"]!);
        Assert.Contains("did not answer", (string)node["error"]!);
        var since = (DateTimeOffset)node["unreachableSince"]!;
        Assert.True(firstCall < AssistantReadMcpTools.NodeBudget + TimeSpan.FromSeconds(3), $"first call took {firstCall}");

        // Within the memory window the node is not asked again, and "since" is still the first failure.
        Assert.Equal(2, second["sessions"]!.AsArray().Count);
        Assert.Equal(since, (DateTimeOffset)second["nodes"]![0]!["unreachableSince"]!);
        await _nodes.Received(1).ReadAsync(Node, Arg.Any<CancellationToken>());
        Assert.True(secondCall < TimeSpan.FromSeconds(1), $"second call took {secondCall}");
    }

    [Fact]
    public async Task StopAgent_OnANodeSession_RefusesNamingTheNode_AndDoesNothing()
    {
        McpRequestContext.Set(AssistantIdentity.PaneId);
        var gateway = Substitute.For<IAssistantAgentGateway>();
        var tools = new AssistantAgentMcpTools(gateway, Substitute.For<IAssistantMemory>());

        var reply = _Json(await tools.StopAgentAsync($"{Node} · {SharedPaneId}"));

        Assert.False((bool)reply["ok"]!);
        Assert.Contains(Node, (string)reply["error"]!);
        await gateway.DidNotReceiveWithAnyArgs().StopAsync(default!);
    }

    public void Dispose() => McpRequestContext.Set(null);
}
