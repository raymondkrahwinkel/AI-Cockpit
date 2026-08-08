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
using Microsoft.Extensions.DependencyInjection;
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

    private AssistantAgentMcpTools _Tools() => new(_gateway, _memory, _consent);

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
        parameter.ParameterType == typeof(string) ? Whatever
            : parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType)
            : null;

    /// <summary>
    /// The acting endpoint as the app actually registers it, resolved the way <c>CockpitMcpEndpointHost</c> resolves
    /// it. Only the endpoint records get constructed — they are registered as instances.
    /// </summary>
    private static CockpitMcpEndpoint _RegisteredActEndpoint() =>
        Assert.Single(
            new ServiceCollection().AddInfrastructure().BuildServiceProvider().GetServices<CockpitMcpEndpoint>(),
            endpoint => endpoint.ServerName == AssistantIdentity.ActMcpServerName);

    /// <summary>
    /// The registered endpoint as the session fan-out sees it — the same projection <c>CockpitMcpEndpointHost</c>
    /// makes. Deriving it is the whole point: a fixture that asserts its own <c>Internal = true</c> is asserting
    /// about the fixture, and would have stayed green through exactly the regression this file exists to catch.
    /// </summary>
    private static McpServerConfig _ActServerAsTheFanOutSeesIt()
    {
        var endpoint = _RegisteredActEndpoint();
        return new McpServerConfig
        {
            Name = endpoint.ServerName,
            Enabled = endpoint.IsEnabled?.Invoke() ?? true,
            CockpitHosted = true,
            Internal = endpoint.Internal,
            AlwaysMounted = endpoint.AlwaysMounted,
        };
    }

    private static McpServerConfig _OrdinaryServer() => new() { Name = "depot", Enabled = true };

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
    public async Task AnOrdinarySessionThatSomehowMountsTheServer_StillCannotCallAnyToolOnIt()
    {
        // The case the fan-out assertions below cannot cover: an internal endpoint IS mounted when a launch names it,
        // so a profile with a hand-edited selection reaches this server. This is why the tools check the pane
        // themselves — the mount is configuration, and the refusal is not.
        var mounted = McpServerRegistryFilter.ApplySessionSelection(
            [_ActServerAsTheFanOutSeesIt()],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { AssistantIdentity.ActMcpServerName });
        Assert.Contains(mounted, server => server.Name == AssistantIdentity.ActMcpServerName);

        McpRequestContext.Set(OrdinarySessionPane);

        foreach (var tool in _EveryTool())
        {
            var result = await _CallAsync(_Tools(), tool);

            Assert.False((bool)result["ok"]!, $"{tool.Name} answered a session that merely mounted the server.");
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

        Assert.Equal(tools.Count, _gateway.Calls.Count + _memory.Remembered.Count + _memory.Noted.Count);
    }

    [Fact]
    public async Task StartAgent_FromTheAssistantsOwnPane_ReachesTheGatewayWithATargetItNamedItself()
    {
        // The typed positive control behind the reflection sweep: the request really is built, the caller's fields
        // really do arrive, and the target carries the assistant's own rule rather than a bare workspace id.
        McpRequestContext.Set(AssistantIdentity.PaneId);

        var result = _Json(await _Tools().StartAgentAsync(
            "ws-release", "Opus", prompt: "write the tests", workingDirectory: @"C:\repo", name: "AC-545 tests"));

        Assert.True((bool)result["ok"]!);
        Assert.Equal("pane-new", (string)result["paneId"]!);

        var request = Assert.Single(_gateway.Spawns);
        Assert.Equal("ws-release", request.Target.WorkspaceId);
        Assert.Equal(SpawnCaller.Assistant, request.Target.Caller);
        Assert.Null(request.Target.CallerPaneId);
        Assert.Equal("Opus", request.ProfileLabel);
        Assert.Equal("write the tests", request.Prompt);
        Assert.Equal(@"C:\repo", request.WorkingDirectory);
        Assert.Equal("AC-545 tests", request.SessionName);
    }

    // ── Criterion 3, first gate: read off the registration, never off a fixture ────────────────────────────────

    [Fact]
    public void TheActingEndpoint_IsRegisteredInternal_SoItNeverFansOutToASessionThatDidNotNameIt()
    {
        // Registered at all (without it the tools are written and nothing hosts them), and registered Internal. This
        // is the assertion the phase-2 fixtures could not make: it fails the moment `Internal: true` leaves
        // DependencyInjection, which is the change that hands start_agent to every session in the cockpit.
        Assert.True(
            _RegisteredActEndpoint().Internal,
            "The assistant's acting endpoint must be Internal, or start_agent fans out to every session.");
    }

    [Fact]
    public void TheActingEndpoint_IsNotAlwaysMounted()
    {
        // AlwaysMounted wins over Internal in McpServerRegistryFilter, and the neighbouring registration
        // (cockpit-agents) is AlwaysMounted — so it is the likeliest thing for this line to become by copy-paste,
        // and it would still read, at a glance, like a deliberate line about the assistant.
        Assert.False(_RegisteredActEndpoint().AlwaysMounted);
    }

    [Fact]
    public void TheActingServer_IsNeverInTheNoSelectionFanOut()
    {
        // A session that named nothing gets every ordinary server and not this one.
        var mounted = McpServerRegistryFilter.ApplySessionSelection(
            [_OrdinaryServer(), _ActServerAsTheFanOutSeesIt()], null);

        Assert.Contains(mounted, server => server.Name == "depot");
        Assert.DoesNotContain(mounted, server => server.Name == AssistantIdentity.ActMcpServerName);
    }

    [Fact]
    public void TheActingServer_IsNeverOfferedToTheOperator()
    {
        // Not something to tick, so not something to tick on the wrong profile.
        var offered = McpServerRegistryFilter.OfferedToOperator([_OrdinaryServer(), _ActServerAsTheFanOutSeesIt()]);

        Assert.DoesNotContain(offered, server => server.Name == AssistantIdentity.ActMcpServerName);
    }

    // ── Criterion 4: the scoping rule a spawn was made under is stamped, not inferred ──────────────────────────

    [Fact]
    public void SpawnTarget_HasExactlyOneDoorPerScopingRule_AndNoOtherWayIn()
    {
        // The claim the type makes about itself, checked rather than trusted: the factories are the only way to
        // build one, and there is one per SpawnCaller. Both sides are read off the source, so a third caller added
        // as a bare enum value with no door of its own — or a door that quietly reuses an existing caller's stamp —
        // fails here instead of landing in the audit trail as somebody else's authority.
        var doors = typeof(SpawnTarget)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(door => door.ReturnType == typeof(SpawnTarget))
            .ToArray();

        Assert.Empty(typeof(SpawnTarget).GetConstructors());
        Assert.Equal(Enum.GetValues<SpawnCaller>().Length, doors.Length);
    }

    [Fact]
    public void SpawnTarget_NamedByTheAssistant_StampsTheAssistant_AndNoPane()
    {
        // The assistant's rule: it named the desk, because it sits on none. The null pane is not an omission — it is
        // the fact that this target was not derived from anything, which is what makes the on-screen consent gate
        // the thing standing behind it.
        var target = SpawnTarget.NamedByTheAssistant("ws-release");

        Assert.Equal("ws-release", target.WorkspaceId);
        Assert.Equal(SpawnCaller.Assistant, target.Caller);
        Assert.Null(target.CallerPaneId);
    }

    [Fact]
    public void SpawnTarget_DerivedFromTheCallersPane_StampsTheCoordinator_AndThePane()
    {
        // The seam AC-436 will come through, asserted now while it is still unused: a host-derived target carries
        // the pane it was derived from, so the two authorities are distinguishable in the trail rather than both
        // reading as "a spawn onto ws-release".
        var target = SpawnTarget.DerivedFromTheCallersPane("ws-release", "pane-coordinator");

        Assert.Equal("ws-release", target.WorkspaceId);
        Assert.Equal(SpawnCaller.Coordinator, target.Caller);
        Assert.Equal("pane-coordinator", target.CallerPaneId);
    }

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

        public Task<AgentStopResult> StopAsync(string paneId, CancellationToken cancellationToken = default)
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

    /// <summary>Clears the ambient pane so one test's caller is never another's.</summary>
    public void Dispose() => McpRequestContext.Set(null);
}
