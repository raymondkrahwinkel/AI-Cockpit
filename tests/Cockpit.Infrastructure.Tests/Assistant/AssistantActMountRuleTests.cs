using System.Reflection;
using System.Text.Json.Nodes;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Assistant;
using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Assistant;
using Cockpit.Infrastructure.Consent;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Plugins.Abstractions.Consent;
using ModelContextProtocol.Server;

namespace Cockpit.Infrastructure.Tests.Assistant;

/// <summary>
/// The mount rule of AC-545: the acting tools — the ones that start processes and spend money — belong to the
/// assistant and to nothing else (criterion 3), and the scoping rule a spawn was made under is recorded rather than
/// inferred (criterion 4).
/// </summary>
/// <remarks>
/// <b>Why "cannot call" and not "is not in the list".</b> Same reason as <see cref="AssistantReadMountRuleTests"/>,
/// with higher stakes: asserting only that the endpoint stays out of the fan-out tests the configuration, and
/// configuration is the half that widens later by accident — an endpoint flipped to non-internal, a profile that
/// names the server, a spawn path that copies a selection it did not read. So every tool on this server is driven
/// <em>directly</em>, as an ordinary session's verified pane, and the assertion is the refusal <em>plus</em> that
/// <see cref="IAssistantAgentGateway"/> was never even asked.
/// <para>
/// <b>Two things in here are derived rather than typed out, and both are a lesson from AC-544's phase 2.</b> First,
/// the tool set: it comes from the <c>[McpServerTool]</c> methods on the class, so a tool added without the
/// pane check fails these tests on the day it is written rather than on the day someone remembers to extend a
/// hand-written list. Second, the server's mount flags: they are read off the endpoint the app actually registers.
/// The phase-2 fan-out tests hand-built an <c>McpServerConfig { Internal = true }</c>, which is a true statement
/// about the filter and no statement at all about the registration — delete <c>Internal: true</c> from
/// <c>DependencyInjection</c> and those tests stay green while the tools fan out to every session.
/// </para>
/// </remarks>
public sealed class AssistantActMountRuleTests : IDisposable
{
    private const string OrdinarySessionPane = "pane-ordinary";

    /// <summary>Something to put in every string parameter. It must never matter, which is most of the point.</summary>
    private const string Whatever = "ws-the-caller-typed";

    private readonly RecordingGateway _gateway = new();

    private readonly ApprovingBroker _consent = new();

    private readonly RecordingAssistantMemory _memory = new();

    private readonly RecordingNodesClient _nodes = new();

    private AssistantAgentMcpTools _Tools() => new(_gateway, _memory, _consent, _nodes);

    private static JsonNode _Json(string result) => JsonNode.Parse(result)!;

    // ── The tool set, and the mount flags, both read off what ships ────────────────────────────────────────────

    /// <summary>
    /// Every tool this server exposes, read off the class rather than listed here — the set is whatever carries
    /// <c>[McpServerTool]</c>, which is exactly the set the MCP host publishes.
    /// </summary>
    private static IReadOnlyList<MethodInfo> _EveryTool() =>
        [.. typeof(AssistantAgentMcpTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(tool => tool.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .OrderBy(tool => tool.Name, StringComparer.Ordinal)];

    /// <summary>
    /// Calls a tool with a filled-in argument for every parameter — including the optional ones, since a refusal
    /// that only holds when the caller left fields blank is not a refusal.
    /// </summary>
    private static async Task<JsonNode> _CallAsync(AssistantAgentMcpTools tools, MethodInfo tool) =>
        _Json(await (Task<string>)tool.Invoke(tools, [.. tool.GetParameters().Select(_Argument)])!);

    private static object? _Argument(ParameterInfo parameter) =>
        // AC-1323: `node` sends a start to another machine instead of the gateway this test counts; left blank,
        // the start stays local. The refusal tests above still fill it, since they never get that far.
        parameter.Name == "node" ? null
            : parameter.Name == "scope" ? "behaviour"
            : parameter.Name == "status" ? "obsolete"
            : parameter.ParameterType == typeof(string) ? Whatever
            : parameter.ParameterType == typeof(List<AskStructuredQuestionOptionArg>)
                // ask_structured_question genuinely requires 2-6 real options — unlike every other list/dict
                // argument on this server, `null` is not a valid filler for it, so it gets one of its own.
                ? new List<AskStructuredQuestionOptionArg> { new("A"), new("B") }
            : parameter.ParameterType == typeof(List<TagPromptPartArg>)
                // tag_prompt refuses an empty list the same way, so it gets one part.
                ? new List<TagPromptPartArg> { new("depot", "de route") }
            : parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType)
            : null;

    /// <summary>
    /// The acting endpoint as the app actually registers it, resolved the way <c>CockpitMcpEndpointHost</c> resolves
    /// it. Only the endpoint records get constructed — they are registered as instances.
    /// </summary>



    // ── Criterion 3: an ordinary agent session cannot start, stop or place anything ────────────────────────────

    [Fact]
    public async Task EveryToolOnTheActingServer_FromAnOrdinaryAgentSession_IsRefused_AndNeverReachesTheGateway()
    {
        // The test that matters. Not "start_agent refuses" — every tool on this server, taken from the class, so the
        // next one cannot be added without its guard and still ship green.
        var tools = _EveryTool();
        Assert.NotEmpty(tools); // A reflection query that found nothing would pass everything below it.

        McpRequestContext.Set(OrdinarySessionPane);

        foreach (var tool in tools)
        {
            var result = await _CallAsync(_Tools(), tool);

            Assert.False((bool)result["ok"]!, $"{tool.Name} answered a session that is not the assistant.");
            Assert.Contains("not available to an agent session", (string)result["error"]!);
        }

        // The half that makes this a test of the guard rather than of an error string: a refusal that had already
        // spawned, stopped or created something first would be a refusal in wording only. These tools cost money and
        // start processes, so "returned an error" is not the assertion — "never got as far as the host" is.
        Assert.Empty(_gateway.Calls);

        // And never got as far as the operator either. The two tools that ask for approval must fail the pane check
        // first: a card raised on behalf of a session that may not be here puts an unauthorised caller's own words on
        // the operator's screen with a button under them.
        Assert.Empty(_consent.Asked);
    }

    [Fact]
    public async Task EveryToolOnTheActingServer_FromARequestWithNoVerifiedPane_IsRefused_AndNeverReachesTheGateway()
    {
        // The shared app-lifetime key path (the in-process tool loop): attributable to no session at all. There is no
        // identity to check, and "I cannot tell who this is" is not an answer that may start a session on any desk.
        var tools = _EveryTool();
        Assert.NotEmpty(tools);

        McpRequestContext.Set(null);

        foreach (var tool in tools)
        {
            var result = await _CallAsync(_Tools(), tool);

            Assert.False((bool)result["ok"]!, $"{tool.Name} answered a request with no verified pane.");
        }

        Assert.Empty(_gateway.Calls);
        Assert.Empty(_consent.Asked);
        Assert.Empty(_memory.Remembered);
        Assert.Empty(_memory.Noted);
    }


    [Fact]
    public async Task EveryToolOnTheActingServer_FromTheAssistantsOwnPane_ReachesTheGateway()
    {
        // The other side of the same guard, and the reason the tests above prove anything: a server that refused
        // everybody would pass every one of them while shipping a feature that does nothing. One call through to
        // what the tool is for — the gateway, or the memory for `remember` — counted against the tool set rather
        // than against a number typed here.
        var tools = _EveryTool();
        Assert.NotEmpty(tools);

        McpRequestContext.Set(AssistantIdentity.PaneId);

        foreach (var tool in tools)
        {
            var result = await _CallAsync(_Tools(), tool);

            Assert.True((bool)result["ok"]!, $"{tool.Name} refused the assistant itself.");
        }

        Assert.Equal(
            tools.Count,
            _gateway.Calls.Count + _memory.Remembered.Count + _memory.Noted.Count + _memory.Exported.Count + _memory.Imported.Count + _nodes.Reads.Count);
    }


    // ── Criterion 3, first gate: read off the registration, never off a fixture ────────────────────────────────



    // ── Criterion 4: the scoping rule a spawn was made under is stamped, not inferred ──────────────────────────




    /// <summary>
    /// A recording stand-in for the host-side spawn service. It records rather than throws so a leak shows up as
    /// "these calls got through" — the tools swallow exceptions into an <c>ok:false</c> result, so a throwing fake
    /// would be indistinguishable from the refusal it is meant to detect the absence of.
    /// </summary>
    private sealed class RecordingGateway : IAssistantAgentGateway
    {
        public List<string> Calls { get; } = [];

        public List<AgentSpawnRequest> Spawns { get; } = [];

        public Task<AgentSpawnResult> SpawnAsync(AgentSpawnRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add($"SpawnAsync({request.Target.Caller} -> {request.Target.WorkspaceId})");
            Spawns.Add(request);
            return Task.FromResult(AgentSpawnResult.Started("pane-new", "AC-545 tests", @"C:\repo"));
        }

        public Task<AgentStopResult> StopAsync(string paneId, SpawnCaller caller = SpawnCaller.Assistant, string? callerPaneId = null, CancellationToken cancellationToken = default)
        {
            Calls.Add($"StopAsync({paneId})");
            return Task.FromResult(AgentStopResult.Stopped(paneId, "AC-545 tests"));
        }

        public Task<AssistantRenameResult> RenameSessionAsync(string paneId, string name, CancellationToken cancellationToken = default)
        {
            Calls.Add($"RenameSessionAsync({paneId} -> {name})");
            return Task.FromResult(AssistantRenameResult.Renamed(name));
        }

        public Task<AssistantRenameResult> RenameWorkspaceAsync(string workspaceId, string name, CancellationToken cancellationToken = default)
        {
            Calls.Add($"RenameWorkspaceAsync({workspaceId} -> {name})");
            return Task.FromResult(AssistantRenameResult.Renamed(name));
        }

        public Task<IReadOnlyList<AssistantWorkspaceRow>> ListWorkspacesAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("ListWorkspacesAsync()");
            return Task.FromResult<IReadOnlyList<AssistantWorkspaceRow>>(
                [new AssistantWorkspaceRow("ws-release", "Release", "sessions", true, 2, false)]);
        }

        public Task<AssistantWorkspaceRow?> CreateWorkspaceAsync(string name, CancellationToken cancellationToken = default)
        {
            Calls.Add($"CreateWorkspaceAsync({name})");
            return Task.FromResult<AssistantWorkspaceRow?>(
                new AssistantWorkspaceRow("ws-new", name, "sessions", true, 0, true));
        }

        public Task<WorkspaceRemovalResult> RemoveWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default)
        {
            Calls.Add($"RemoveWorkspaceAsync({workspaceId})");
            return Task.FromResult(WorkspaceRemovalResult.Removed("Release"));
        }

        public Task<IReadOnlyList<AssistantProfileRow>> ListProfilesAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("ListProfilesAsync()");
            return Task.FromResult<IReadOnlyList<AssistantProfileRow>>(
                [new AssistantProfileRow("Claude Test", "Claude", "sonnet")]);
        }

        public Task<AgentMessageResult> SendMessageAsync(string paneId, string kind, string body, CancellationToken cancellationToken = default)
        {
            Calls.Add($"SendMessageAsync({paneId})");
            return Task.FromResult(AgentMessageResult.Sent(paneId, "AC-545 tests", "msg-1", deduplicated: false, deliversAtTurnStart: true));
        }

        public Task<AgentPromptResult> SendPromptAsync(string paneId, string prompt, CancellationToken cancellationToken = default)
        {
            Calls.Add($"SendPromptAsync({paneId})");
            return Task.FromResult(AgentPromptResult.Handed(paneId, "AC-545 tests", delivered: true));
        }

        public Task<AssistantWatchResult> WatchSessionAsync(
            string paneId,
            IReadOnlyList<string>? events,
            int? afterMinutes = null,
            string? pattern = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"WatchSessionAsync({paneId})");
            return Task.FromResult(AssistantWatchResult.Watched("AC-545 tests"));
        }

        public Task<bool> UnwatchSessionAsync(string paneId, CancellationToken cancellationToken = default)
        {
            Calls.Add($"UnwatchSessionAsync({paneId})");
            return Task.FromResult(true);
        }

        public Task<WorktreeHandoverResult> HandoverWorktreeAsync(string path, string paneId, CancellationToken cancellationToken = default)
        {
            Calls.Add($"HandoverWorktreeAsync({path} -> {paneId})");
            return Task.FromResult(WorktreeHandoverResult.HandedOver(path, "cockpit/handed-over", "AC-545 tests"));
        }

        public Task<OpenUrlResult> OpenUrlAsync(string url, CancellationToken cancellationToken = default)
        {
            Calls.Add($"OpenUrlAsync({url})");
            return Task.FromResult(OpenUrlResult.Opened(url));
        }

        public Task<ClearConversationResult> RequestConversationClearAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("RequestConversationClearAsync()");
            return Task.FromResult(ClearConversationResult.Requested(alreadyQueued: false));
        }

        public Task<AssistantProjectBindResult> BindSharedProjectAsync(
            string sharedProjectId,
            string sourceDirectory,
            string profileLabel,
            IReadOnlyList<string>? resourceReferences = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"BindSharedProjectAsync({sharedProjectId} -> {sourceDirectory})");
            return Task.FromResult(
                AssistantProjectBindResult.Bound("local-1", "Marketing site", "Depot — Work", sourceDirectory));
        }

        public Task<AssistantProjectCreateResult> CreateProjectAsync(
            string name,
            string? description = null,
            string? sourceDirectory = null,
            string? defaultProfileLabel = null,
            string? behaviorPrompt = null,
            bool isolateInWorktreeByDefault = false,
            IReadOnlyList<string>? enabledMcpServerNames = null,
            string? category = null,
            IReadOnlyDictionary<string, string>? pluginFields = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"CreateProjectAsync({name})");
            return Task.FromResult(AssistantProjectCreateResult.Created("local-1", name));
        }

        // Not logged to `Calls` — `UpdateProjectAsync` below is "the" call the mount-rule test counts per tool,
        // the same way `list_projects`' own read gateway sits outside this one.
        public Task<AssistantProjectSnapshot?> GetProjectSnapshotAsync(string projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult<AssistantProjectSnapshot?>(new AssistantProjectSnapshot(
                projectId, "Project", null, null, null, null, false, null, null, new Dictionary<string, string>(), null, null));

        public Task<AssistantProjectUpdateResult> UpdateProjectAsync(
            string projectId,
            string? name = null,
            string? description = null,
            string? sourceDirectory = null,
            string? defaultProfileLabel = null,
            string? behaviorPrompt = null,
            bool? isolateInWorktreeByDefault = null,
            IReadOnlyList<string>? enabledMcpServerNames = null,
            string? category = null,
            IReadOnlyDictionary<string, string>? pluginFields = null,
            string? gitUrl = null,
            string? memoryRef = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"UpdateProjectAsync({projectId})");
            return Task.FromResult(AssistantProjectUpdateResult.Updated(projectId, name ?? "Project"));
        }

        public Task<AskStructuredQuestionResult> AskStructuredQuestionAsync(
            string question, IReadOnlyList<(string Label, string? Description)> options, bool multiSelect, bool allowOther,
            string? header, CancellationToken cancellationToken = default)
        {
            Calls.Add($"AskStructuredQuestionAsync({question})");
            return Task.FromResult(AskStructuredQuestionResult.Shown());
        }

        public Task<IReadOnlyList<AssistantOpenQuestion>> ListOpenQuestionsAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("ListOpenQuestionsAsync()");
            return Task.FromResult<IReadOnlyList<AssistantOpenQuestion>>([]);
        }

        public Task<AssistantThreadResult> ResolveQuestionAsync(
            string questionId, string status, string? note, CancellationToken cancellationToken = default)
        {
            Calls.Add($"ResolveQuestionAsync({questionId})");
            return Task.FromResult(new AssistantThreadResult(true, null));
        }

        public Task<AssistantThreadResult> SetThreadAsync(IReadOnlyList<string> projectIds, CancellationToken cancellationToken = default)
        {
            Calls.Add("SetThreadAsync");
            return Task.FromResult(new AssistantThreadResult(true, null));
        }

        public Task<AssistantThreadResult> TagPromptAsync(
            IReadOnlyList<(string ProjectId, string Quote)> parts, CancellationToken cancellationToken = default)
        {
            Calls.Add("TagPromptAsync");
            return Task.FromResult(new AssistantThreadResult(true, null));
        }

        public Task<IReadOnlyList<AssistantThreadInfo>> ListThreadsAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("ListThreadsAsync");
            return Task.FromResult<IReadOnlyList<AssistantThreadInfo>>([]);
        }

        // The check before the operator is asked; not a call through to what close_thread is for, so not counted.
        public Task<string?> WhyThreadCannotCloseAsync(string projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<AssistantThreadResult> CloseThreadAsync(string projectId, CancellationToken cancellationToken = default)
        {
            Calls.Add($"CloseThreadAsync({projectId})");
            return Task.FromResult(new AssistantThreadResult(true, null));
        }

        public Task<AssistantThreadResult> ReopenThreadAsync(string projectId, CancellationToken cancellationToken = default)
        {
            Calls.Add($"ReopenThreadAsync({projectId})");
            return Task.FromResult(new AssistantThreadResult(true, null));
        }

        public Task<AssistantThreadResult> SuggestClosingThreadsAsync(IReadOnlyList<string> projectIds, CancellationToken cancellationToken = default)
        {
            Calls.Add("SuggestClosingThreadsAsync");
            return Task.FromResult(new AssistantThreadResult(true, null));
        }
    }

    /// <summary>
    /// An operator who says yes to everything. The two tools that ask cannot reach the gateway without an answer, so
    /// the sweeps above would otherwise be measuring the absence of a broker rather than the pane gate — and the
    /// refusal sweeps assert that this one is never even consulted.
    /// </summary>
    private sealed class ApprovingBroker : IConsentBroker
    {
        public List<ConsentRequest> Asked { get; } = [];

        public event EventHandler<ConsentPrompt>? PromptOpened;

        public event EventHandler<Guid>? PromptClosed;

        public Task<ConsentDecision> RequestConsentAsync(ConsentRequest request, CancellationToken cancellationToken = default)
        {
            Asked.Add(request);
            // Referenced so the compiler does not warn the events are never raised; nothing here opens a prompt.
            PromptOpened?.Invoke(this, null!);
            PromptClosed?.Invoke(this, Guid.Empty);
            return Task.FromResult(new ConsentDecision(ConsentOutcome.Approved, false));
        }

        public void Respond(Guid promptId, ConsentOutcome outcome, bool remember)
        {
        }
    }

    /// <summary>
    /// AC-1329: a stand-in for the paired-node line, wide enough for this file's reflection sweep — `remember`'s
    /// behaviour path lists zero nodes, and <c>read_node_memory</c> is the one tool here that actually reads
    /// through it. Every other member is unused by anything this class calls with the sweep's filled-in arguments.
    /// </summary>
    private sealed class RecordingNodesClient : INodeSessionsClient
    {
        public List<(string Node, string Scope)> Reads { get; } = [];

        public Task<IReadOnlyList<string>> ListNodesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<NodeMemoryRead> ReadMemoryAsync(string nodeName, string scope, CancellationToken cancellationToken = default)
        {
            Reads.Add((nodeName, scope));
            return Task.FromResult(new NodeMemoryRead("what this node knows"));
        }

        public Task<NodeSessionsSnapshot> ReadAsync(string nodeName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public (NodeSessionsSnapshot Snapshot, DateTimeOffset AtUtc)? TryGetLastSnapshot(string nodeName) => throw new NotSupportedException();

        public Task<NodeStartResult> StartAsync(string nodeName, string profileLabel, string? projectId = null, string? prompt = null, string? sessionName = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string?> StopAsync(string nodeName, string paneId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string?> SendPromptAsync(string nodeName, string paneId, string prompt, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string?> SendMessageAsync(string nodeName, string paneId, string kind, string body, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string?> RenameAsync(string nodeName, string paneId, string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<NodeTranscriptRead> ReadTranscriptAsync(string nodeName, string paneId, int count, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string?> RememberOnNodeAsync(string nodeName, string text, string scope, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<NodeConnectProbe> ProbeAsync(McpServerConfig row, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<NodeWhoAmI?> ReadWhoAmIAsync(string nodeName, CancellationToken cancellationToken = default) => Task.FromResult<NodeWhoAmI?>(null);
    }

    /// <summary>Clears the ambient pane so one test's caller is never another's.</summary>
    public void Dispose() => McpRequestContext.Set(null);
}
