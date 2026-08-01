using System.Text.Json.Nodes;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Assistant;
using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Agents;
using Cockpit.Infrastructure.Assistant;
using Cockpit.Infrastructure.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Assistant;

/// <summary>
/// The mount rule of AC-544: the broad read tools belong to the assistant and to nothing else (criterion 2), and
/// making them exist did not loosen the workspace scoping every other agent relies on (criterion 3).
/// </summary>
/// <remarks>
/// <b>Why "cannot call" and not "is not in the list".</b> Asserting only that the endpoint stays out of the fan-out
/// tests the configuration, and configuration is the half that widens later by accident — an endpoint flipped to
/// non-internal, a profile that names the server, a spawn path that copies a selection it did not read. Every one of
/// those leaves the fan-out assertions passing and the protection gone. So the tests below drive the tools
/// <em>directly</em>, as an ordinary session's verified pane, and assert the refusal and that the gateway was never
/// even asked — which is what a test of this rule has to fail on when the guard is deleted.
/// </remarks>
public sealed class AssistantReadMountRuleTests : IDisposable
{
    private const string OrdinarySessionPane = "pane-ordinary";

    private readonly IAssistantReadGateway _gateway = Substitute.For<IAssistantReadGateway>();

    private AssistantReadMcpTools _Tools() => new(_gateway);

    private static JsonNode _Json(string result) => JsonNode.Parse(result)!;

    /// <summary>The endpoint as <c>DependencyInjection</c> registers it, so these tests move when that registration does.</summary>
    private static McpServerConfig _AssistantServer() =>
        new() { Name = AssistantIdentity.McpServerName, Enabled = true, CockpitHosted = true, Internal = true };

    private static McpServerConfig _OrdinaryServer() =>
        new() { Name = "depot", Enabled = true };

    // ── Criterion 2: an ordinary agent session does not have them, and cannot call them ────────────────────────

    [Fact]
    public async Task ListSessions_FromAnOrdinaryAgentSession_IsRefused_AndNeverReachesTheGateway()
    {
        McpRequestContext.Set(OrdinarySessionPane);

        var result = _Json(await _Tools().ListSessionsAsync());

        Assert.False((bool)result["ok"]!);
        Assert.Contains("not available to an agent session", (string)result["error"]!);

        // The half that makes this a test of the guard rather than of an error string: a refusal that still read
        // every workspace first would have leaked exactly what the rule exists to withhold.
        await _gateway.DidNotReceive().ListSessionsAsync();
    }

    [Fact]
    public async Task ListSessions_FromARequestWithNoVerifiedPane_IsRefused()
    {
        // The shared app-lifetime key path (the in-process tool loop): attributable to no session at all. There is
        // no identity to check, and "I cannot tell who this is" is not an answer that may open every workspace.
        McpRequestContext.Set(null);

        var result = _Json(await _Tools().ListSessionsAsync());

        Assert.False((bool)result["ok"]!);
        await _gateway.DidNotReceive().ListSessionsAsync();
    }

    [Fact]
    public async Task ListSessions_FromTheAssistantsOwnPane_Answers()
    {
        // The other side of the same guard: a rule that refused everyone would pass every test above and ship a
        // feature that does nothing.
        _gateway.ListSessionsAsync().Returns(Task.FromResult<IReadOnlyList<AssistantSessionRow>>(
            [new AssistantSessionRow("pane-1", "AC-223", "Opus", "AC-223 — writing tests", "ws-2", "Cockpit")]));
        McpRequestContext.Set(AssistantIdentity.PaneId);

        var result = _Json(await _Tools().ListSessionsAsync());

        Assert.True((bool)result["ok"]!);
        var session = result["sessions"]!.AsArray()[0]!;
        Assert.Equal("AC-223 — writing tests", (string)session["statusline"]!);
        Assert.Equal("Cockpit", (string)session["workspaceName"]!);
        Assert.True((bool)session["hasStatusline"]!);
    }

    [Fact]
    public void TheBroadReadServer_IsNeverInTheNoSelectionFanOut()
    {
        // The first of the two gates: a session that named nothing gets every ordinary server and not this one.
        var mounted = McpServerRegistryFilter.ApplySessionSelection([_OrdinaryServer(), _AssistantServer()], null);

        Assert.Contains(mounted, server => server.Name == "depot");
        Assert.DoesNotContain(mounted, server => server.Name == AssistantIdentity.McpServerName);
    }

    [Fact]
    public void TheBroadReadServer_IsNeverOfferedToTheOperator()
    {
        // Not something to tick, so not something to tick on the wrong profile.
        var offered = McpServerRegistryFilter.OfferedToOperator([_OrdinaryServer(), _AssistantServer()]);

        Assert.DoesNotContain(offered, server => server.Name == AssistantIdentity.McpServerName);
    }

    [Fact]
    public async Task AnOrdinarySessionThatSomehowMountsTheServer_StillCannotCallIt()
    {
        // The case the fan-out assertions above cannot cover: an internal endpoint IS mounted when a launch names
        // it, so a profile with a hand-edited selection reaches this server. This is why the tools check the pane
        // themselves — the mount is configuration, and the refusal is not.
        var mounted = McpServerRegistryFilter.ApplySessionSelection(
            [_AssistantServer()],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { AssistantIdentity.McpServerName });
        Assert.Contains(mounted, server => server.Name == AssistantIdentity.McpServerName);

        McpRequestContext.Set(OrdinarySessionPane);
        var result = _Json(await _Tools().ListSessionsAsync());

        Assert.False((bool)result["ok"]!);
        await _gateway.DidNotReceive().ListSessionsAsync();
    }

    // ── Criterion 3: list_agents is unchanged, and still workspace-scoped ──────────────────────────────────────

    [Fact]
    public async Task ListAgents_StillRefusesACallerTheHostPlacesOnNoWorkspace()
    {
        // The assistant is exactly such a caller (SessionWorkspacePlacement places it nowhere, by construction), and
        // this is the refusal AC-544 was tempted to relax. If someone ever makes cockpit-agents answer a caller with
        // no desk, this fails — which is the whole point of writing it down.
        var workspaces = Substitute.For<IWorkspaceAgentGateway>();
        workspaces.GetWorkspaceSnapshotAsync(Arg.Any<string>()).Returns(Task.FromResult<WorkspaceAgentSnapshot?>(null));
        McpRequestContext.Set(AssistantIdentity.PaneId);

        var result = _Json(await _AgentsTools(workspaces).ListAgentsAsync());

        Assert.False((bool)result["ok"]!);
    }

    [Fact]
    public async Task ListAgents_StillReportsOnlyTheCallersOwnDesk()
    {
        // The scoping itself: the roster is derived from the caller's own snapshot, so a pane on another desk is not
        // in it and there is no argument that could put it there — ListAgentsAsync takes none.
        var workspaces = Substitute.For<IWorkspaceAgentGateway>();
        workspaces.GetWorkspaceSnapshotAsync("pane-a").Returns(Task.FromResult<WorkspaceAgentSnapshot?>(
            new WorkspaceAgentSnapshot("ws-a", [new WorkspaceAgentPane("pane-a", "A", null, string.Empty, true)])));
        McpRequestContext.Set("pane-a");

        var result = _Json(await _AgentsTools(workspaces).ListAgentsAsync());

        Assert.True((bool)result["ok"]!);
        Assert.Equal("ws-a", (string)result["workspaceId"]!);
        var panes = result["agents"]!.AsArray().Select(agent => (string)agent!["paneId"]!).ToArray();
        Assert.Equal(["pane-a"], panes);
    }

    private AgentsMcpTools _AgentsTools(IWorkspaceAgentGateway workspaces) =>
        new(workspaces, new WorkspaceAgentCoordinator(), new AgentMessageInbox(),
            new AgentNotifyAuditLog(_auditPath, NullLogger<AgentNotifyAuditLog>.Instance), new AgentResourceClaims());

    private readonly string _auditPath =
        Path.Combine(Path.GetTempPath(), $"assistant-mount-audit-{Guid.NewGuid():N}.jsonl");

    /// <summary>Clears the ambient pane so one test's caller is never another's, and takes the trail file with it.</summary>
    public void Dispose()
    {
        McpRequestContext.Set(null);
        if (File.Exists(_auditPath))
        {
            File.Delete(_auditPath);
        }
    }
}
