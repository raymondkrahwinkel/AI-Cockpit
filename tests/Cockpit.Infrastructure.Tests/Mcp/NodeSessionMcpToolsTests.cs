using System.Reflection;
using System.Text.Json.Nodes;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Mcp;
using ModelContextProtocol.Server;

namespace Cockpit.Infrastructure.Tests.Mcp;

/// <summary>
/// What a paired controller may do with this machine's sessions (AC-795): criterion 2 — starting and stopping,
/// but only within <c>[e]</c>'s grant — and criterion 5, that nothing else on this machine can reach these tools
/// at all.
/// </summary>
/// <remarks>
/// <b>Why the refusal is asserted per tool and not once.</b> Same reason <see cref="Assistant.AssistantActMountRuleTests"/>
/// gives for the assistant's own server: the mount rule (an endpoint whose <c>IsEnabled</c> is false, so no local
/// session is offered it) is configuration, and configuration widens by accident. So every <c>[McpServerTool]</c>
/// on the class is driven directly as an ordinary session's verified pane, and the assertion is the refusal
/// <em>plus</em> that neither gateway was asked — a tool added later without the check fails this on the day it is
/// written.
/// </remarks>
public sealed class NodeSessionMcpToolsTests : IDisposable
{
    private const string OrdinarySessionPane = "pane-ordinary";

    private const string AllowedProfile = "Laptop Sonnet";

    private const string AllowedProject = "project-allowed";

    private readonly RecordingAgentGateway _gateway = new();

    private readonly RecordingReadGateway _read = new();

    private readonly StubPairing _pairing = new();

    private NodeSessionMcpTools _Tools() =>
        new(_read, _gateway, _pairing, new StubProfileStore());

    private static JsonNode _Json(string result) => JsonNode.Parse(result)!;

    // Every tool this server exposes, read off the class rather than listed here.
    private static IReadOnlyList<MethodInfo> _EveryTool() =>
        [.. typeof(NodeSessionMcpTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(tool => tool.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .OrderBy(tool => tool.Name, StringComparer.Ordinal)];

    private static async Task<JsonNode> _CallAsync(NodeSessionMcpTools tools, MethodInfo tool) =>
        _Json(await (Task<string>)tool.Invoke(tools, [.. tool.GetParameters().Select(_Argument)])!);

    private static object? _Argument(ParameterInfo parameter) =>
        parameter.ParameterType == typeof(string) ? "whatever"
            : parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType)
            : null;

    [Fact]
    public async Task EveryTool_RefusesAnOrdinarySessionOnThisMachine_AndAsksNoGateway()
    {
        McpRequestContext.Set(OrdinarySessionPane);
        _pairing.AllowEverything = true;

        foreach (var tool in _EveryTool())
        {
            var answer = await _CallAsync(_Tools(), tool);

            Assert.False(answer["ok"]!.GetValue<bool>());
            Assert.Contains("paired to this one as its controller", answer["error"]!.GetValue<string>(), StringComparison.Ordinal);
        }

        // The refusal came before anything was touched — not after a spawn it then reported as failed.
        Assert.Empty(_gateway.Calls);
        Assert.Empty(_read.Calls);
    }

    [Fact]
    public async Task EveryTool_RefusesTheInProcessToolLoop_WhichCarriesNoPaneAtAll()
    {
        // null is what the cockpit's own tool loop carries. It is not the node's reserved identity, so it is not
        // the controller either — the check is an equality, never "anything but a session".
        McpRequestContext.Set(null);
        _pairing.AllowEverything = true;

        foreach (var tool in _EveryTool())
        {
            Assert.False((await _CallAsync(_Tools(), tool))["ok"]!.GetValue<bool>());
        }

        Assert.Empty(_gateway.Calls);
    }

    [Fact]
    public async Task Start_UnderAnUntickedProfile_IsRefusedAndNothingIsStarted()
    {
        McpRequestContext.Set(NodeCallerIdentity.PaneId);

        var answer = _Json(await _Tools().StartNodeAgentAsync("Something Expensive"));

        Assert.False(answer["ok"]!.GetValue<bool>());
        // Criterion 2: the grant is the gate, and the refusal names what to go and tick rather than saying "no".
        Assert.Contains("Something Expensive", answer["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.DoesNotContain(_gateway.Calls, call => call.StartsWith("SpawnAsync", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Start_ForAnUntickedProject_IsRefusedEvenWhenTheProfileIsAllowed()
    {
        McpRequestContext.Set(NodeCallerIdentity.PaneId);
        _pairing.Profiles.Add(AllowedProfile);

        var answer = _Json(await _Tools().StartNodeAgentAsync(AllowedProfile, "project-not-ticked"));

        Assert.False(answer["ok"]!.GetValue<bool>());
        Assert.Contains("project-not-ticked", answer["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.DoesNotContain(_gateway.Calls, call => call.StartsWith("SpawnAsync", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Start_WithinTheGrant_RunsOnThisMachinesActiveDesk_UnderTheControllersOwnRule()
    {
        McpRequestContext.Set(NodeCallerIdentity.PaneId);
        _pairing.Profiles.Add(AllowedProfile);
        _pairing.Projects.Add(AllowedProject);

        var answer = _Json(await _Tools().StartNodeAgentAsync(AllowedProfile, AllowedProject, "run the sweep", "sweep"));

        Assert.True(answer["ok"]!.GetValue<bool>());

        var spawn = Assert.Single(_gateway.Spawns);
        // The desk was read here, never received: the controller named none and could not have.
        Assert.Equal("ws-active", spawn.Target.WorkspaceId);
        Assert.Equal(SpawnCaller.Controller, spawn.Target.Caller);
        Assert.Equal(NodeCallerIdentity.PaneId, spawn.Target.CallerPaneId);
        // And nothing about this machine's filesystem crossed the wire in either direction.
        Assert.Null(spawn.WorkingDirectory);
    }

    [Fact]
    public async Task ListProfiles_ShowsOnlyTheTickedOnes_AndOnlyThreeFieldsOfEach()
    {
        McpRequestContext.Set(NodeCallerIdentity.PaneId);
        _pairing.Profiles.Add(AllowedProfile);

        var answer = _Json(await _Tools().ListNodeProfilesAsync());

        var profiles = answer["profiles"]!.AsArray();
        var only = Assert.Single(profiles);
        Assert.Equal(AllowedProfile, only!["label"]!.GetValue<string>());
        // The provider crosses as its name, not as whatever number the enum happens to carry today.
        Assert.Equal(nameof(SessionProvider.ClaudeCli), only["provider"]!.GetValue<string>());
        Assert.Equal(["label", "provider", "purpose"], only.AsObject().Select(field => field.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Stop_ActsOnThePaneIdItWasGiven_NotOnAName()
    {
        McpRequestContext.Set(NodeCallerIdentity.PaneId);
        _pairing.Profiles.Add(AllowedProfile);

        // Criterion 3, at this end: two sessions on this machine carry the same name, and the only thing that tells
        // them apart is the pane id — which is why nothing here looks a session up by name.
        _read.Sessions.Add(new AssistantSessionRow("pane-a", "AC-795 tests", AllowedProfile, "", null, null));
        _read.Sessions.Add(new AssistantSessionRow("pane-b", "AC-795 tests", AllowedProfile, "", null, null));

        var answer = _Json(await _Tools().StopNodeAgentAsync("pane-b"));

        Assert.True(answer["ok"]!.GetValue<bool>());
        Assert.Equal("StopAsync(pane-b)", Assert.Single(_gateway.Calls, call => call.StartsWith("StopAsync", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ListAndStop_CoverOnlyTheWorkRunningUnderAnAllowedProfile()
    {
        McpRequestContext.Set(NodeCallerIdentity.PaneId);
        _pairing.Profiles.Add(AllowedProfile);
        _read.Sessions.Add(new AssistantSessionRow("pane-mine", "the sweep", AllowedProfile, "", null, null));
        // The node operator's own work, under a profile they never ticked for this controller.
        _read.Sessions.Add(new AssistantSessionRow("pane-theirs", "their own work", "Something Expensive", "", null, null));

        var listed = _Json(await _Tools().ListNodeSessionsAsync())["sessions"]!.AsArray();
        var stop = _Json(await _Tools().StopNodeAgentAsync("pane-theirs"));

        // Seeing and stopping are the same set on purpose: a fresh pairing with nothing ticked could otherwise end
        // every agent on the machine while being allowed to start none.
        Assert.Equal("pane-mine", Assert.Single(listed)!["paneId"]!.GetValue<string>());
        Assert.False(stop["ok"]!.GetValue<bool>());
        Assert.DoesNotContain(_gateway.Calls, call => call.StartsWith("StopAsync", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Start_UnderALabelThatDiffersOnlyInCase_IsCheckedAgainstTheProfileThatWouldActuallyRun()
    {
        McpRequestContext.Set(NodeCallerIdentity.PaneId);
        // Only the lower-cased twin is ticked. The spawn path resolves case-insensitively, so checking the string
        // as it arrived would pass a grant the resolved profile does not have.
        _pairing.Profiles.Add("laptop sonnet");

        var answer = _Json(await _Tools().StartNodeAgentAsync(AllowedProfile));

        Assert.False(answer["ok"]!.GetValue<bool>());
        Assert.DoesNotContain(_gateway.Calls, call => call.StartsWith("SpawnAsync", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stop_IsRecordedAsTheController_NotAsThisMachinesOwnAssistant()
    {
        McpRequestContext.Set(NodeCallerIdentity.PaneId);
        _pairing.Profiles.Add(AllowedProfile);
        _read.Sessions.Add(new AssistantSessionRow("pane-mine", "the sweep", AllowedProfile, "", null, null));

        await _Tools().StopNodeAgentAsync("pane-mine");

        // A stop that arrived from another machine and is written down as this cockpit's own assistant is a trail
        // that reads plausibly and is wrong.
        Assert.Equal((SpawnCaller.Controller, NodeCallerIdentity.PaneId), Assert.Single(_gateway.Stops));
    }

    [Fact]
    public async Task NothingHere_StopsASessionWhenThePairingEnds()
    {
        // Criterion 4, and Raymond's decision behind it: this is offloading, not remote control. A session started
        // by a controller is not the controller's to lose — unpairing revokes the credential and leaves the work.
        // The tempting "tidy up on unpair" would make this the other thing, silently, so it is pinned here.
        McpRequestContext.Set(NodeCallerIdentity.PaneId);
        _pairing.Profiles.Add(AllowedProfile);
        await _Tools().StartNodeAgentAsync(AllowedProfile);

        _pairing.Unpair();

        Assert.DoesNotContain(_gateway.Calls, call => call.StartsWith("StopAsync", StringComparison.Ordinal));

        // And the far side is now refused, which is what revocation does mean.
        var answer = _Json(await _Tools().StartNodeAgentAsync(AllowedProfile));
        Assert.False(answer["ok"]!.GetValue<bool>());
    }

    public void Dispose() => McpRequestContext.Set(null);

    // Internal rather than private: `NodeSessionsClientRealNetworkTests` (AC-796) reuses these four fakes to host a
    // real `NodeSessionMcpTools` behind a real TLS listener, instead of redeclaring the same stand-ins twice.
    internal sealed class StubPairing : INodePairingBroker
    {
        public HashSet<string> Profiles { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Projects { get; } = new(StringComparer.Ordinal);

        // Only for the refusal tests, where the point is that the pane check runs before the grant is ever read.
        public bool AllowEverything { get; set; }

        public NodePairing? Pairing => null;

        public NodePairingPending? Pending => null;

        public event EventHandler? Changed;

        public bool IsProfileAllowed(string profileLabel) => AllowEverything || Profiles.Contains(profileLabel);

        public bool IsProjectAllowed(string projectId) => AllowEverything || Projects.Contains(projectId);

        public void Unpair()
        {
            Profiles.Clear();
            Projects.Clear();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public Task<NodePairingOffer> RequestAsync(string controllerName, string controllerAddress, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ConfirmAsync(string pairingId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void Refuse(string pairingId) => throw new NotSupportedException();

        public Task<NodePairingGrant> ClaimAsync(string pairingId, string claimToken, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UnpairAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task EnsureLoadedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SetScopeAsync(IReadOnlyList<string> allowedProfileLabels, IReadOnlyList<string> allowedProjectIds, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    internal sealed class StubProfileStore : ISessionProfileStore
    {
        public Task<IReadOnlyList<SessionProfile>> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SessionProfile>>(
            [
                new SessionProfile(AllowedProfile, new ClaudeConfig("/fake/.claude"), "the laptop's own key"),
                new SessionProfile("Something Expensive", new ClaudeConfig("/fake/.claude")),
            ]);

        public Task SaveAsync(IReadOnlyList<SessionProfile> profiles, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    internal sealed class RecordingReadGateway : IAssistantReadGateway
    {
        public List<string> Calls { get; } = [];

        public List<AssistantSessionRow> Sessions { get; } = [];

        public Task<IReadOnlyList<AssistantSessionRow>> ListSessionsAsync()
        {
            Calls.Add("ListSessionsAsync()");
            return Task.FromResult<IReadOnlyList<AssistantSessionRow>>([.. Sessions]);
        }

        public Task<IReadOnlyList<AssistantProjectRow>> ListProjectsAsync()
        {
            Calls.Add("ListProjectsAsync()");
            return Task.FromResult<IReadOnlyList<AssistantProjectRow>>(
                [new AssistantProjectRow(AllowedProject, "Allowed", null, null, null, new Dictionary<string, string>(), null, [])]);
        }

        public Task<AssistantTranscript?> ReadTranscriptAsync(string paneId, int count) => throw new NotSupportedException();

        public Task<IReadOnlyList<AssistantSharedProjectSourceRow>> ListSharedProjectsAsync() => throw new NotSupportedException();
    }

    internal sealed class RecordingAgentGateway : IAssistantAgentGateway
    {
        public List<string> Calls { get; } = [];

        public List<AgentSpawnRequest> Spawns { get; } = [];

        public Task<AgentSpawnResult> SpawnAsync(AgentSpawnRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add($"SpawnAsync({request.Target.Caller} -> {request.Target.WorkspaceId})");
            Spawns.Add(request);
            return Task.FromResult(AgentSpawnResult.Started("pane-new", "the sweep", null, true, request.ProfileLabel));
        }

        public List<(SpawnCaller Caller, string? CallerPaneId)> Stops { get; } = [];

        public Task<AgentStopResult> StopAsync(string paneId, SpawnCaller caller = SpawnCaller.Assistant, string? callerPaneId = null, CancellationToken cancellationToken = default)
        {
            Calls.Add($"StopAsync({paneId})");
            Stops.Add((caller, callerPaneId));
            return Task.FromResult(AgentStopResult.Stopped(paneId, "the sweep"));
        }

        public Task<IReadOnlyList<AssistantWorkspaceRow>> ListWorkspacesAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("ListWorkspacesAsync()");
            return Task.FromResult<IReadOnlyList<AssistantWorkspaceRow>>(
            [
                // A desk that cannot hold a session, one that can but is not showing, and the active one — so
                // "the active desk that can host" is actually being chosen rather than "the first row".
                new AssistantWorkspaceRow("ws-terminals", "Terminals", "terminals", false, 0, false),
                new AssistantWorkspaceRow("ws-other", "Other", "sessions", true, 1, false),
                new AssistantWorkspaceRow("ws-active", "Release", "sessions", true, 2, true),
            ]);
        }

        public Task<AssistantRenameResult> RenameSessionAsync(string paneId, string name, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AssistantRenameResult> RenameWorkspaceAsync(string workspaceId, string name, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AssistantWorkspaceRow?> CreateWorkspaceAsync(string name, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WorkspaceRemovalResult> RemoveWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<AssistantProfileRow>> ListProfilesAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AgentMessageResult> SendMessageAsync(string paneId, string kind, string body, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AgentPromptResult> SendPromptAsync(string paneId, string prompt, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AssistantWatchResult> WatchSessionAsync(
            string paneId,
            IReadOnlyList<string>? on = null,
            int? quietSeconds = null,
            string? note = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> UnwatchSessionAsync(string paneId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WorktreeHandoverResult> HandoverWorktreeAsync(string path, string paneId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<OpenUrlResult> OpenUrlAsync(string url, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AssistantProjectBindResult> BindSharedProjectAsync(
            string sourceName,
            string sharedProjectId,
            string localName,
            IReadOnlyList<string>? mcpServers = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AssistantProjectCreateResult> CreateProjectAsync(
            string name,
            string? description = null,
            string? sourceDirectory = null,
            string? gitUrl = null,
            string? defaultProfileLabel = null,
            bool isolateInWorktree = false,
            IReadOnlyList<string>? mcpServers = null,
            string? behaviorPrompt = null,
            IReadOnlyDictionary<string, string>? links = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AssistantProjectSnapshot?> GetProjectSnapshotAsync(string projectId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

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
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AskStructuredQuestionResult> AskStructuredQuestionAsync(
            string question, IReadOnlyList<(string Label, string? Description)> options, bool multiSelect, bool allowOther,
            string? header, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
