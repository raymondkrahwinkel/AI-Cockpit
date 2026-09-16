using System.Text.Json.Nodes;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Agents;
using Cockpit.Infrastructure.Mcp;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Mcp;

// AC-1324: the node's end of a permission answered from the controller's screen — the open questions ride on
// list_node_sessions, and answer_node_permission carries the click back, bounded by the pairing's scope.
public sealed class NodePermissionAnswerTests : IDisposable
{
    private const string Pane = "0123456789abcdef0123456789abcdef";

    private const string PrivatePane = "fedcba9876543210fedcba9876543210";

    private static readonly DateTimeOffset Noon = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly IAssistantReadGateway _read = Substitute.For<IAssistantReadGateway>();

    private readonly IAssistantAgentGateway _gateway = Substitute.For<IAssistantAgentGateway>();

    private readonly NodeSessionMcpToolsTests.StubPairing _pairing = new() { Profiles = { "Laptop Sonnet" } };

    public NodePermissionAnswerTests()
    {
        McpRequestContext.Set(NodeCallerIdentity.PaneId);
        _read.ListSessionsAsync().Returns(
        [
            new AssistantSessionRow(Pane, "AC-1", "Laptop Sonnet", "", "ws", "Sessions", "NeedsAttention", NeedsYou: true),
            new AssistantSessionRow(PrivatePane, "private", "Something Expensive", "", "ws", "Sessions", "NeedsAttention", NeedsYou: true),
        ]);
        _read.ListPendingPermissionsAsync().Returns(
        [
            new AssistantPendingPermission(Pane, "toolu_1", "Bash", "{\"command\":\"dotnet build\"}", Noon),
            new AssistantPendingPermission(PrivatePane, "toolu_2", "Bash", "{\"command\":\"rm -rf /\"}", Noon),
        ]);
    }

    private NodeSessionMcpTools _Tools() => new(
        _read, _gateway, _pairing, new NodeSessionMcpToolsTests.StubProfileStore(),
        new NodeDiscoveryId(Path.Combine(Path.GetTempPath(), $"node-discovery-id-{Guid.NewGuid():N}.txt")), new AgentMessageInbox());

    private static JsonNode _Json(string result) => JsonNode.Parse(result)!;

    // Criterion 2, the node's half: the question a visible session is stopped on is listed with it (and a session
    // outside the scope, question and all, is not); the click reaches the gateway under an allowed profile, is
    // refused before it under one that is not, and a question no longer open comes back as not answered.
    [Fact]
    public async Task AnOpenQuestion_IsListedWithItsSession_AndAnsweredThroughTheGateway_WithinTheScope()
    {
        var listed = _Json(await _Tools().ListNodeSessionsAsync());
        var session = Assert.Single(listed["sessions"]!.AsArray());
        Assert.Equal(Pane, (string)session!["paneId"]!);
        var question = Assert.Single(session["pendingPermissions"]!.AsArray());
        Assert.Equal("toolu_1", (string)question!["toolUseId"]!);
        Assert.Equal("Bash", (string)question["tool"]!);
        Assert.Contains("dotnet build", (string)question["input"]!);
        Assert.Equal(Noon, (DateTimeOffset)question["sinceUtc"]!);

        _gateway.RespondToPermissionAsync(Pane, "toolu_1", true, Arg.Any<CancellationToken>()).Returns(true);
        var answered = _Json(await _Tools().AnswerNodePermissionAsync(Pane, "toolu_1", allow: true));
        Assert.True((bool)answered["ok"]!, answered.ToJsonString());
        Assert.True((bool)answered["answered"]!);
        Assert.Equal("Allowed", (string)answered["outcome"]!);
        await _gateway.Received(1).RespondToPermissionAsync(Pane, "toolu_1", true, Arg.Any<CancellationToken>());

        _gateway.RespondToPermissionAsync(Pane, "toolu_1", true, Arg.Any<CancellationToken>()).Returns(false);
        var gone = _Json(await _Tools().AnswerNodePermissionAsync(Pane, "toolu_1", allow: true));
        Assert.True((bool)gone["ok"]!);
        Assert.False((bool)gone["answered"]!);
        Assert.Contains("no longer open", (string)gone["outcome"]!);

        var refused = _Json(await _Tools().AnswerNodePermissionAsync(PrivatePane, "toolu_2", allow: true));
        Assert.False((bool)refused["ok"]!);
        Assert.Contains("allowed", (string)refused["error"]!);
        await _gateway.DidNotReceive().RespondToPermissionAsync(PrivatePane, Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    public void Dispose() => McpRequestContext.Set(null);
}
