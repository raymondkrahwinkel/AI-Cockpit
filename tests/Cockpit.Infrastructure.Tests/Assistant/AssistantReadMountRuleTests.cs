using System.Text.Json.Nodes;
using System.Net;
using System.Text;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Assistant;
using Cockpit.Core.Delegation;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
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

    private readonly IDelegationService _delegation = Substitute.For<IDelegationService>();

    private AssistantReadMcpTools _Tools(INodeSessionsClient? nodes = null, LocalTextModelClient? localText = null) =>
        new(_gateway, _delegation, nodes ?? Substitute.For<INodeSessionsClient>(), new NodeDiscoveryId(Path.Combine(Path.GetTempPath(), $"node-discovery-id-{Guid.NewGuid():N}.txt")), ModelOptionsTests.PictureReader(), localText);

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
        var profiles = Substitute.For<ISessionProfileStore>();
        using var http = new HttpClient(new _LocalModelHandler());
        var local = _Tools(localText: new LocalTextModelClient(profiles, http));
        Assert.False((bool)_Json(await local.LocalSummarizeAsync("summarize", "private"))["ok"]!);
        await profiles.DidNotReceive().LoadAsync(Arg.Any<CancellationToken>());

        McpRequestContext.Set(null);

        result = _Json(await _Tools().ListSessionsAsync());

        Assert.False((bool)result["ok"]!);
        await _gateway.DidNotReceive().ListSessionsAsync();
        Assert.False((bool)_Json(await local.LocalSummarizeAsync("summarize", "private"))["ok"]!);
        await profiles.DidNotReceive().LoadAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListSessions_FromTheAssistantsOwnPane_Answers()
    {
        // The other side of the same guard: a rule that refused everyone would pass every test above and ship a
        // feature that does nothing.
        _gateway.ListSessionsAsync().Returns(Task.FromResult<IReadOnlyList<AssistantSessionRow>>(
            [new AssistantSessionRow("pane-1", "AC-223", "Opus", "AC-223 — writing tests", "ws-2", "Cockpit")]));
        var nodes = Substitute.For<INodeSessionsClient>();
        nodes.ListNodesAsync().Returns(Task.FromResult<IReadOnlyList<string>>(["node-a"]));
        nodes.ReadAsync("node-a", Arg.Any<CancellationToken>()).Returns(Task.FromResult(
            new NodeSessionsSnapshot("node-a", [], [new NodeScopedProfileSummary("Node profile", SessionProvider.ClaudeCli, null)], [new NodeProjectRow("node-project", "Node project")])));
        McpRequestContext.Set(AssistantIdentity.PaneId);

        var result = _Json(await _Tools(nodes).ListSessionsAsync());

        Assert.True((bool)result["ok"]!);
        var session = result["sessions"]!.AsArray()[0]!;
        Assert.Equal("AC-223 — writing tests", (string)session["statusline"]!);
        Assert.Equal("Cockpit", (string)session["workspaceName"]!);
        Assert.True((bool)session["hasStatusline"]!);
        var node = result["nodes"]!.AsArray()[0]!;
        Assert.False(node.AsObject().ContainsKey("profiles"));
        Assert.False(node.AsObject().ContainsKey("projects"));

        var withCatalog = _Json(await _Tools(nodes).ListSessionsAsync(includeNodeCatalog: true));
        var catalogNode = withCatalog["nodes"]!.AsArray()[0]!;
        Assert.Equal("Node profile", (string)catalogNode["profiles"]!.AsArray()[0]!);
        Assert.Equal("node-project", (string)catalogNode["projects"]!.AsArray()[0]!["id"]!);
    }

    [Fact]
    public async Task LocalSummarize_UsesOnlyLoadedLocalModel_AndReportsBudgetAndValidationFailures()
    {
        McpRequestContext.Set(AssistantIdentity.PaneId);
        var profiles = Substitute.For<ISessionProfileStore>();
        profiles.LoadAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<SessionProfile>>(
            [new SessionProfile("LM Coder (Qwen3.8 27B)", new LmStudioConfig("http://127.0.0.1:1234", "qwen/qwen3.8-27b"))]));
        var handler = new _LocalModelHandler();
        using var http = new HttpClient(handler);
        var tools = _Tools(localText: new LocalTextModelClient(profiles, http));
        foreach (var kind in new[] { "summarize", "analyze_log", "draft_comment", "clean_dictation", "brief" })
        {
            var answer = _Json(await tools.LocalSummarizeAsync(kind, "De build is groen; review staat nog open.", 30));
            Assert.True((bool)answer["ok"]!);
            Assert.Equal("answered", (string)answer["outcome"]!);
            Assert.Equal("Build groen; review open.", (string)answer["text"]!);
            Assert.True((long)answer["elapsedMilliseconds"]! >= 0);
            Assert.Equal(42, (int)answer["tokens"]!["completion_tokens"]!);
            Assert.Equal(1024, (int)handler.LastBody!["max_tokens"]!);
            Assert.Equal("low", (string)handler.LastBody["reasoning_effort"]!);
            Assert.Equal("qwen/qwen3.8-27b", (string)handler.LastBody["model"]!);
            Assert.False(handler.LastBody.AsObject().ContainsKey("tools"));
            Assert.Contains("30 words", (string)handler.LastBody["messages"]![0]!["content"]!);
        }
        handler.Finish = "length";
        handler.Answer = "";
        var exhausted = _Json(await tools.LocalSummarizeAsync("summarize", "source", thinkingBudget: 17));
        Assert.Equal("budget_exhausted", (string)exhausted["outcome"]!);
        Assert.Equal(LocalTextModelClient.NoAnswer, (string)exhausted["text"]!);
        Assert.Equal(17, (int)handler.LastBody!["max_tokens"]!);
        handler.Answer = "unfinished draft";
        Assert.Equal(LocalTextModelClient.NoAnswer, (string)_Json(await tools.LocalSummarizeAsync("brief", "source"))["text"]!);
        handler.Finish = "stop";
        handler.Answer = " ";
        Assert.Equal(LocalTextModelClient.NoAnswer, (string)_Json(await tools.LocalSummarizeAsync("brief", "source"))["text"]!);

        var before = handler.Calls;
        foreach (var kind in new[] { "classify", "decide", "review" })
        {
            Assert.False((bool)_Json(await tools.LocalSummarizeAsync(kind, "source"))["ok"]!);
        }
        Assert.False((bool)_Json(await tools.LocalSummarizeAsync("brief", ""))["ok"]!);
        Assert.False((bool)_Json(await tools.LocalSummarizeAsync("brief", new string('a', 80_001)))["ok"]!);
        Assert.False((bool)_Json(await tools.LocalSummarizeAsync("brief", "source", wordLimit: 0))["ok"]!);
        Assert.False((bool)_Json(await tools.LocalSummarizeAsync("brief", "source", thinkingBudget: 4097))["ok"]!);
        Assert.Equal(before, handler.Calls);

        foreach (var url in new[] { "https://example.com", "http://localhost.evil.test", "http://192.168.1.2:1234", "http://localhost:1234/proxy" })
        {
            profiles.LoadAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<SessionProfile>>(
                [new SessionProfile("LM Coder", new LmStudioConfig(url, "qwen/qwen3.8-27b"))]));
            Assert.False((bool)_Json(await tools.LocalSummarizeAsync("brief", "source"))["ok"]!);
        }
        Assert.Equal(before, handler.Calls);
        profiles.LoadAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<SessionProfile>>(
            [new SessionProfile("LM Coder", new LmStudioConfig("http://localhost:1234", "missing"))]));
        var missing = _Json(await tools.LocalSummarizeAsync("brief", "source"));
        Assert.False((bool)missing["ok"]!);
        Assert.Contains("not already loaded", (string)missing["error"]!);
        Assert.Equal(before + 1, handler.Calls);
        profiles.LoadAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<SessionProfile>>(
            [new SessionProfile("LM Coder", new LmStudioConfig("http://localhost:1234", "qwen/qwen3.8-27b"))]));
        handler.Failure = HttpStatusCode.ServiceUnavailable;
        before = handler.Calls;
        Assert.False((bool)_Json(await tools.LocalSummarizeAsync("brief", "source"))["ok"]!);
        Assert.Equal(before + 2, handler.Calls);
        handler.Failure = null;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tools.LocalSummarizeAsync("brief", "source", cancellationToken: cancelled.Token));
        before = handler.Calls;
        profiles.LoadAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<SessionProfile>>([]));
        Assert.False((bool)_Json(await tools.LocalSummarizeAsync("brief", "source"))["ok"]!);
        Assert.Equal(before, handler.Calls);
    }

    /// <summary>
    /// The gap this ticket exists to close: <see cref="ListSessions_FromTheAssistantsOwnPane_Answers"/> above checks
    /// three fields by name and would not notice a fourth silently missing — which is exactly how <c>status</c> and
    /// <c>needsYou</c> went missing from the wire the first time even though <see cref="AssistantSessionRow"/> and
    /// the description both already promised them. This test pins the whole shape instead, and derives what "whole"
    /// means from <see cref="AssistantSessionRow"/>'s own properties via reflection rather than a second, hand-typed
    /// list of field names — so a field added to the row later and never wired into the projection fails this test
    /// immediately instead of shipping unnoticed a second time.
    /// </summary>
    [Fact]
    public async Task ListSessions_JsonShape_CoversEveryFieldOnAssistantSessionRow()
    {
        _gateway.ListSessionsAsync().Returns(Task.FromResult<IReadOnlyList<AssistantSessionRow>>(
            [new AssistantSessionRow("pane-1", "AC-223", "Opus", "AC-223 — writing tests", "ws-2", "Cockpit", "Busy", true, true)]));
        McpRequestContext.Set(AssistantIdentity.PaneId);

        var result = _Json(await _Tools().ListSessionsAsync());
        var actualKeys = ((JsonObject)result["sessions"]!.AsArray()[0]!).Select(field => field.Key).ToHashSet();

        // Every record property, camelCased the way System.Text.Json's default naming would — plus hasStatusline,
        // the one field that is derived (an empty-string check) rather than a property of the row itself, and
        // machine, the origin AC-1320 stamps on every row rather than carrying in the row.
        var expectedKeys = typeof(AssistantSessionRow).GetProperties()
            .Select(property => char.ToLowerInvariant(property.Name[0]) + property.Name[1..])
            .Append("hasStatusline")
            .Append("machine")
            .ToHashSet();

        Assert.Equal(expectedKeys, actualKeys);
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

    [Fact]
    public async Task ReadTranscript_FromAnOrdinaryAgentSession_IsRefused_AndNeverReachesTheGateway()
    {
        // The same rule as list_sessions, asserted separately because it is a separate tool: a guard added once and
        // then forgotten on the next tool is exactly how this server would quietly stop being the assistant's own.
        // A transcript is the sharper case of the two — one agent reading another's whole conversation.
        McpRequestContext.Set(OrdinarySessionPane);

        var result = _Json(await _Tools().ReadTranscriptAsync("pane-1"));

        Assert.False((bool)result["ok"]!);
        Assert.Contains("not available to an agent session", (string)result["error"]!);
        await _gateway.DidNotReceive().ReadTranscriptAsync(Arg.Any<string>(), Arg.Any<int>());
    }

    [Fact]
    public async Task ReadTranscript_NamingTheCallersOwnPane_IsStillRefused()
    {
        // The argument is not a way in. An ordinary session naming any pane at all — including its own, which it is
        // entirely entitled to read by other means — gets nothing from this server, because what is checked is the
        // pane the transport stamped and never the pane that was typed.
        McpRequestContext.Set(OrdinarySessionPane);

        var result = _Json(await _Tools().ReadTranscriptAsync(OrdinarySessionPane));

        Assert.False((bool)result["ok"]!);
        await _gateway.DidNotReceive().ReadTranscriptAsync(Arg.Any<string>(), Arg.Any<int>());
    }

    [Fact]
    public async Task ReadTranscript_FromTheAssistantsOwnPane_GetsTheEntries()
    {
        _gateway.ReadTranscriptAsync("pane-1", Arg.Any<int>()).Returns(Task.FromResult<AssistantTranscript?>(
            new AssistantTranscript("pane-1", "AC-223", 2,
            [
                new AssistantTranscriptEntry("UserText", "run the tests", null),
                new AssistantTranscriptEntry("ToolUse", "Tool: Bash({\"command\":\"dotnet test\"})", "3 failed"),
            ])));
        McpRequestContext.Set(AssistantIdentity.PaneId);

        var result = _Json(await _Tools().ReadTranscriptAsync("pane-1"));

        Assert.True((bool)result["ok"]!);
        Assert.Equal("AC-223", (string)result["name"]!);
        var entries = result["entries"]!.AsArray();
        Assert.Equal("UserText", (string)entries[0]!["kind"]!);
        Assert.Equal("run the tests", (string)entries[0]!["text"]!);
        // The coupled result, which is where a tool call's substance actually lives — a reader that dropped it would
        // report every command this session ran and nothing any of them said.
        Assert.Equal("3 failed", (string)entries[1]!["toolResult"]!);
        Assert.Equal(0, (int)result["omitted"]!);
        Assert.Null(result["more"]);
    }

    [Fact]
    public async Task ReadTranscript_StripsTerminalControlSequences()
    {
        // A transcript is the most agent-authored text in the cockpit and it lands in a reply the assistant's own
        // runtime prints. Reused from the agent-message path rather than rewritten here.
        _gateway.ReadTranscriptAsync("pane-1", Arg.Any<int>()).Returns(Task.FromResult<AssistantTranscript?>(
            new AssistantTranscript("pane-1", "AC-223", 1,
                [new AssistantTranscriptEntry("AssistantText", "done\u001b[2Jwiped", null)])));
        McpRequestContext.Set(AssistantIdentity.PaneId);

        var result = _Json(await _Tools().ReadTranscriptAsync("pane-1"));

        Assert.DoesNotContain('\u001b', (string)result["entries"]!.AsArray()[0]!["text"]!);
    }

    // ── AC-797: list_shared_projects ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListSharedProjects_FromAnOrdinaryAgentSession_IsRefused_AndNeverReachesTheGateway()
    {
        McpRequestContext.Set(OrdinarySessionPane);

        var result = _Json(await _Tools().ListSharedProjectsAsync());

        Assert.False((bool)result["ok"]!);
        await _gateway.DidNotReceive().ListSharedProjectsAsync();
    }

    // ── AC-641: the delegated work list_sessions cannot see ───────────────────────────────────────────────────

    private static DelegatedTaskView _Task(
        string taskId, DelegatedTaskStatus status = DelegatedTaskStatus.Running, string? ownerPaneId = "pane-1") =>
        new(taskId, "Sonnet", "review the diff", "review", status, DateTimeOffset.Now, DateTimeOffset.Now, null, 2,
            null, null, ownerPaneId);

    [Fact]
    public void ListDelegatedTasks_FromAnOrdinaryAgentSession_IsRefused_AndNeverReachesTheService()
    {
        McpRequestContext.Set(OrdinarySessionPane);

        var result = _Json(_Tools().ListDelegatedTasks());

        Assert.False((bool)result["ok"]!);
        Assert.Contains("not available to an agent session", (string)result["error"]!);
        _delegation.DidNotReceive().ListTasks(Arg.Any<DelegatedTaskStatus?>(), Arg.Any<string?>());
    }

    [Fact]
    public void ListDelegatedTasks_ReadsUnscoped_SoItSeesEveryOwnersTasksAndNotTheAssistantsOwnNone()
    {
        // The whole point of the tool: passing the caller's pane through the way list_tasks does would scope the read
        // to the assistant's own tasks, and the assistant creates none — so it would always answer "nothing running"
        // while five delegated sessions worked.
        _delegation.ListTasks(null, null).Returns([_Task("t1"), _Task("t2", ownerPaneId: "pane-2")]);
        McpRequestContext.Set(AssistantIdentity.PaneId);

        var result = _Json(_Tools().ListDelegatedTasks());

        Assert.True((bool)result["ok"]!);
        Assert.Equal(2, (int)result["count"]!);
        _delegation.Received(1).ListTasks(null, null);
    }

    /// <summary>
    /// The same lesson as <see cref="ListSessions_JsonShape_CoversEveryFieldOnAssistantSessionRow"/>, applied to the
    /// projection this tool has to write by hand: a field added to <see cref="DelegatedTaskView"/> later and never
    /// wired in here goes missing from the wire silently, and the description keeps promising it.
    /// </summary>
    [Fact]
    public void ListDelegatedTasks_JsonShape_CoversEveryFieldOnDelegatedTaskView()
    {
        _delegation.ListTasks(null, null).Returns([_Task("t1")]);
        McpRequestContext.Set(AssistantIdentity.PaneId);

        var result = _Json(_Tools().ListDelegatedTasks());
        var actualKeys = ((JsonObject)result["tasks"]!.AsArray()[0]!).Select(field => field.Key).ToHashSet();

        var expectedKeys = typeof(DelegatedTaskView).GetProperties()
            .Select(property => char.ToLowerInvariant(property.Name[0]) + property.Name[1..])
            .ToHashSet();

        Assert.Equal(expectedKeys, actualKeys);
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

    private sealed class _LocalModelHandler : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        internal JsonNode? LastBody { get; private set; }
        internal string Finish { get; set; } = "stop";
        internal string Answer { get; set; } = "Build groen; review open.";
        internal HttpStatusCode? Failure { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Assert.True(request.RequestUri!.IsLoopback);
            if (request.Method == HttpMethod.Get)
            {
                Assert.Equal("/api/v1/models", request.RequestUri.AbsolutePath);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                    "{\"models\":[{\"type\":\"llm\",\"loaded_instances\":[{\"id\":\"qwen/qwen3.8-27b\"}]}]}", Encoding.UTF8, "application/json") };
            }
            Assert.Equal("/v1/chat/completions", request.RequestUri.AbsolutePath);
            LastBody = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (Failure is { } failure)
            {
                return new HttpResponseMessage(failure);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = Finish,
                    message = new { content = Answer, reasoning_content = "private reasoning" } } },
                    usage = new { prompt_tokens = 13, completion_tokens = 42, total_tokens = 55 } }), Encoding.UTF8, "application/json") };
        }
    }

    private AgentsMcpTools _AgentsTools(IWorkspaceAgentGateway workspaces) =>
        new(workspaces, new WorkspaceAgentCoordinator(), new AgentMessageInbox(),
            new AgentNotifyAuditLog(_auditPath, NullLogger<AgentNotifyAuditLog>.Instance), new AgentResourceClaims(),
            new AgentLineBudget());

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
