using System.Diagnostics;
using System.Text.Json.Nodes;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Assistant;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Assistant;
using Cockpit.Infrastructure.Mcp;
using NSubstitute;
using Xunit.Abstractions;

namespace Cockpit.Infrastructure.Tests.Assistant;

// AC-1320: the assistant reads the sessions of a paired node through list_sessions, every row stamped with the
// machine it runs on. Acting on one of those rows is AC-1323 (AssistantNodeControlTests).
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

    // AC-1326 criterion 1, plus its counterproof as the fourth row: a shared project (same Project.Id on both
    // machines) is one row naming both; a local-only and a node-only project are their own rows; two projects
    // that merely share a NAME with different ids never collapse into one — id is the only merge key.
    [Theory]
    [InlineData("p1", "Alpha", "p1", "Alpha", "p1", 1, true, true)]
    [InlineData("p2", "Beta", "p9", "Zeta", "p2", 2, true, false)]
    [InlineData("p8", "Omega", "p3", "Gamma", "p3", 2, false, true)]
    [InlineData("p4", "Delta", "p5", "Delta", "p4", 2, true, false)]
    public async Task ListProjects_MergesOnlyByProjectId_NeverByName(
        string localId, string localName, string nodeId, string nodeName,
        string checkRowId, int expectedRowCount, bool expectHere, bool expectNode)
    {
        McpRequestContext.Set(AssistantIdentity.PaneId);
        _read.ListProjectsAsync().Returns([new AssistantProjectRow(localId, localName, null, null, null, new Dictionary<string, string>(), null, [])]);
        _nodes.ListNodesAsync(Arg.Any<CancellationToken>()).Returns([Node]);
        _nodes.ReadAsync(Node, Arg.Any<CancellationToken>()).Returns(new NodeSessionsSnapshot(
            Node, [], [], [new NodeProjectRow(nodeId, nodeName)], DiscoveryId: "NODE-ID"));

        var reply = _Json(await _ReadTools().ListProjectsAsync());
        var rows = reply["projects"]!.AsArray();

        Assert.Equal(expectedRowCount, rows.Count);
        var row = Assert.Single(rows, candidate => (string)candidate!["Id"]! == checkRowId);
        var runsOn = row!["runsOn"]!.AsArray().Select(entry => (string)entry!).ToList();
        Assert.Equal(expectHere, runsOn.Contains(Environment.MachineName));
        Assert.Equal(expectNode, runsOn.Contains(Node));
    }

    // AC-1326 criterion 2, with its counterproof folded in: a label that exists on both machines stays two rows,
    // and machine is what tells them apart — there is nothing else here that would.
    [Fact]
    public async Task ListProfiles_WithTheSameLabelOnBothMachines_StaysTwoRows_ToldApartByMachine()
    {
        McpRequestContext.Set(AssistantIdentity.PaneId);
        var gateway = Substitute.For<IAssistantAgentGateway>();
        gateway.ListProfilesAsync(Arg.Any<CancellationToken>()).Returns([new AssistantProfileRow("Sonnet", "Claude", "sonnet")]);
        _nodes.ListNodesAsync(Arg.Any<CancellationToken>()).Returns([Node]);
        _nodes.TryGetLastSnapshot(Node).Returns((
            new NodeSessionsSnapshot(Node, [], [new NodeScopedProfileSummary("Sonnet", SessionProvider.ClaudeCli, null)], []),
            DateTimeOffset.UtcNow));

        var tools = new AssistantAgentMcpTools(gateway, Substitute.For<IAssistantMemory>(), nodes: _nodes);
        var reply = _Json(await tools.ListProfilesAsync());
        var rows = reply["profiles"]!.AsArray();

        Assert.Equal(2, rows.Count);
        var local = Assert.Single(rows, row => (bool)row!["machine"]!["local"]!);
        var remote = Assert.Single(rows, row => !(bool)row!["machine"]!["local"]!);
        Assert.Equal("Sonnet", (string)local!["Label"]!);
        Assert.Equal("Sonnet", (string)remote!["Label"]!);
        Assert.Equal(Environment.MachineName, (string)local["machine"]!["name"]!);
        Assert.Equal(Node, (string)remote!["machine"]!["name"]!);
    }

    public void Dispose() => McpRequestContext.Set(null);
}
