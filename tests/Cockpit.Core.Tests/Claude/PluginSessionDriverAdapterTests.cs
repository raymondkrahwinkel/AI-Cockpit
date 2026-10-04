using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Mcp;
using Cockpit.Core.Sessions;
using Cockpit.Core.Sessions.Permissions;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;
using NSubstitute;

namespace Cockpit.Core.Tests.Claude;

/// <summary>
/// <see cref="PluginSessionDriverAdapter"/> (#45): wraps a <see cref="FakePluginSessionDriver"/> and proves
/// it satisfies <c>ISessionDriver</c> by forwarding every real member and mapping each
/// <see cref="PluginSessionEvent"/> subtype to its <see cref="SessionEvent"/> counterpart. The
/// Claude-CLI-only live-control members (permission mode / model / thinking budget) have no equivalent on
/// the narrow interface and must be safe no-ops rather than throwing.
/// </summary>
public class PluginSessionDriverAdapterTests
{
    private static readonly McpAuthKey _authKey = new();

    [Fact]
    public async Task StartAsync_AProfileVariableOnAHostControlledKey_NeverCrossesThePluginBoundary()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);
        var profile = new SessionProfile("work", new ClaudeConfig("/config/dir"))
        {
            EnvironmentVariables = [new ProfileEnvironmentVariable("ANTHROPIC_API_KEY", "smuggled", IsSecret: true)],
        };

        await adapter.StartAsync(profile);

        Assert.False(inner.LastEnvironment!.ContainsKey("ANTHROPIC_API_KEY"));
    }

    // The same rule the profile's variables meet, applied where the value is put in the driver's environment rather
    // than trusted to have been applied upstream — the resolver and the merge both scrub too, and this is what still
    // holds if either of them ever stops.
    [Fact]
    public async Task StartAsync_AContributedVariableOnAHostControlledKey_NeverCrossesThePluginBoundary()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(
            inner, inner.Capabilities, _authKey,
            sessionResources: StubResources(("ANTHROPIC_API_KEY", "smuggled"), ("GH_REPO", "owner/repo")));

        await adapter.StartAsync(new SessionProfile("work", new ClaudeConfig("/config/dir")), launchOptions: PaneOptions);

        Assert.False(inner.LastEnvironment!.ContainsKey("ANTHROPIC_API_KEY"));
        Assert.Contains("GH_REPO", inner.LastEnvironment!);
    }

    // A contribution must not be able to rename the session it is running in: the pane id is the identity the consent
    // broker and the session-status tool attribute by, so one a plugin chose would let it act as another pane.
    [Fact]
    public async Task StartAsync_AContributedPaneId_NeverReachesTheDriver()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(
            inner, inner.Capabilities, _authKey,
            sessionResources: StubResources(("COCKPIT_PANE_ID", "someone-elses-pane")));

        await adapter.StartAsync(new SessionProfile("work", new ClaudeConfig("/config/dir")), launchOptions: PaneOptions);

        Assert.False(inner.LastEnvironment!.ContainsKey("COCKPIT_PANE_ID"));
    }

    // With no resolver in the graph (every other test here, and any host built before AC-165) the launch is exactly
    // what it was: the parameter is optional precisely so an existing composition keeps working untouched.
    /// <summary>
    /// AC-89: the session's MCP identity dies with the session. Without this the token stays a valid bearer for every
    /// cockpit-hosted endpoint until the app restarts, still naming a pane that is gone — and the consent broker keys
    /// remembered approvals on exactly that pane id.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_RevokesTheTokenItMintedForTheSession()
    {
        var keyring = new SessionMcpKeyring();
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey, keyring: keyring);
        await adapter.StartAsync(launchOptions: PaneOptions);
        Assert.NotNull(inner.LastEnvironment);
        var token = inner.LastEnvironment[WellKnownSessionEnvironment.CockpitMcpKey];
        Assert.Equal("pane-1", keyring.PaneFor(token));

        await adapter.DisposeAsync();

        Assert.Null(keyring.PaneFor(token));
    }

    /// <summary>
    /// The restart race at the level it would actually happen: two adapters on one pane sharing a keyring, the second
    /// already started when the first is disposed. The keyring's own test pins the same rule, but only by calling
    /// Revoke directly — this is the shape the rule exists for.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_AfterThePaneHasStartedAgain_LeavesTheLiveSessionsTokenAlone()
    {
        var keyring = new SessionMcpKeyring();
        var closing = new FakePluginSessionDriver();
        var first = new PluginSessionDriverAdapter(closing, closing.Capabilities, _authKey, keyring: keyring);
        await first.StartAsync(launchOptions: PaneOptions);
        var second = new FakePluginSessionDriver();
        var restarted = new PluginSessionDriverAdapter(second, second.Capabilities, _authKey, keyring: keyring);
        await restarted.StartAsync(launchOptions: PaneOptions);
        Assert.NotNull(second.LastEnvironment);
        var live = second.LastEnvironment[WellKnownSessionEnvironment.CockpitMcpKey];

        await first.DisposeAsync();

        Assert.Equal("pane-1", keyring.PaneFor(live));
    }

    /// <summary>
    /// A session on the shared app key has no token of its own, and the app key is not this adapter's to drop — it is
    /// the whole app's baseline capability, and revoking it would take every other session's access with it.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_WithNoPaneId_LeavesTheSharedAppKeyAlone()
    {
        var keyring = new SessionMcpKeyring();
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey, keyring: keyring);
        await adapter.StartAsync();
        Assert.NotNull(inner.LastEnvironment);
        Assert.Equal(_authKey.Value, inner.LastEnvironment[WellKnownSessionEnvironment.CockpitMcpKey]);

        var act = async () => await adapter.DisposeAsync();

        await act();
    }

    private static readonly IReadOnlyDictionary<string, string> PaneOptions =
        new Dictionary<string, string> { [WellKnownPluginSessionOptions.PaneId] = "pane-1" };

    private static ISessionResourceResolver StubResources(params (string Key, string Value)[] variables)
    {
        var resolver = Substitute.For<ISessionResourceResolver>();
        resolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new SessionResources(variables.ToDictionary(
                variable => variable.Key, variable => variable.Value, StringComparer.Ordinal)));
        return resolver;
    }

    // AC-40: every spawned session carries this run's MCP auth key in its environment, so a cockpit-hosted server's
    // config can reference COCKPIT_MCP_KEY instead of embedding a literal — even a profile with no variables of its own.
    [Fact]
    public async Task StartAsync_AlwaysPassesTheMcpAuthKeyToTheDriver()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);

        await adapter.StartAsync(new SessionProfile("work", new ClaudeConfig("/config/dir")));

        Assert.Contains(WellKnownSessionEnvironment.CockpitMcpKey, inner.LastEnvironment!);
    }

    // AC-190: a provider that confines to its working directory via a real OS sandbox (Codex — ConfinesViaPermissionsOnly
    // left false) confines in every permission mode. The static registration capability maps straight through, unchanged.
    private static PluginSessionCapabilities _SandboxConfining() =>
        new(SupportsTools: true, SupportsPermissions: true) { ConfinesFileAccessToWorkingDirectory = true };

    // AC-190: a provider whose confinement rests on its permission system (Claude) — a bypass mode disables the guard,
    // so the adapter must vouch confinement only for a permission-engaged mode.
    private static PluginSessionCapabilities _PermissionConfining() =>
        new(SupportsTools: true, SupportsPermissions: true) { ConfinesFileAccessToWorkingDirectory = true, ConfinesViaPermissionsOnly = true };

    [Fact]
    public async Task Capabilities_ForAPermissionBasedConfiningProvider_ReportsUnconfined_WhenStartedInBypass()
    {
        var inner = new FakePluginSessionDriver { Capabilities = _PermissionConfining() };
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);

        // bypassPermissions (--dangerously-skip-permissions) disables the permission guard the confinement leans on, so
        // the isolate-in-worktree gate must see this session as NOT confined and refuse it — the AC-190 fail-closed fix.
        // Proven red before the fix: the adapter copied the static "true" registration capability regardless of mode.
        await adapter.StartAsync(permissionMode: "bypassPermissions");

        Assert.False(adapter.Capabilities.ConfinesFileAccessToWorkingDirectory);
    }

    [Theory]
    [InlineData("acceptEdits")]
    [InlineData("default")]
    [InlineData("plan")]
    public async Task Capabilities_ForAPermissionBasedConfiningProvider_ReportsConfined_WhenStartedInAPermissionEngagedMode(string mode)
    {
        var inner = new FakePluginSessionDriver { Capabilities = _PermissionConfining() };
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);

        // The permission system stays engaged in these modes, so cwd-bound tools remain confined — the isolated run is
        // allowed to proceed. acceptEdits is the shipped Autopilot default (the interim mitigation), so this must pass.
        await adapter.StartAsync(permissionMode: mode);

        Assert.True(adapter.Capabilities.ConfinesFileAccessToWorkingDirectory);
    }

    [Fact]
    public async Task Capabilities_ForAPermissionBasedConfiningProvider_ReportsConfined_WhenStartedWithNoExplicitMode()
    {
        var inner = new FakePluginSessionDriver { Capabilities = _PermissionConfining() };
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);

        // No permission mode selected falls back to the driver's own default (which confines) — not a bypass, so confined.
        await adapter.StartAsync();

        Assert.True(adapter.Capabilities.ConfinesFileAccessToWorkingDirectory);
    }

    [Fact]
    public async Task Capabilities_ForAPermissionBasedConfiningProvider_ReportsUnconfined_ForAnUnrecognisedMode()
    {
        var inner = new FakePluginSessionDriver { Capabilities = _PermissionConfining() };
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);

        // Allowlist, not denylist (fail closed): a mode the adapter does not recognise as permission-engaged is treated
        // as not confining, so a future/unknown mode is refused until reviewed rather than silently trusted.
        await adapter.StartAsync(permissionMode: "yolo");

        Assert.False(adapter.Capabilities.ConfinesFileAccessToWorkingDirectory);
    }

    [Fact]
    public void Capabilities_ForAPermissionBasedConfiningProvider_ReportsUnconfined_BeforeStart()
    {
        var inner = new FakePluginSessionDriver { Capabilities = _PermissionConfining() };
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);

        // Fail closed before the permission mode is resolved: an isolation gate that read the capability before start
        // must not be told the session is confined on an assumption. (The host reads it after start; this guards the seam.)
        Assert.False(adapter.Capabilities.ConfinesFileAccessToWorkingDirectory);
    }

    [Fact]
    public async Task Capabilities_ForASandboxConfiningProvider_StaysConfined_EvenInBypass()
    {
        var inner = new FakePluginSessionDriver { Capabilities = _SandboxConfining() };
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);

        // Codex confines via a real OS sandbox, independent of its permission/approval mode — it must NOT be downgraded
        // by the AC-190 permission-mode check. Regression guard that the fix touches only permission-based providers.
        await adapter.StartAsync(permissionMode: "bypassPermissions");

        Assert.True(adapter.Capabilities.ConfinesFileAccessToWorkingDirectory);
    }

    [Fact]
    public async Task Capabilities_ForASandboxConfiningProvider_IsConfined_BeforeStart()
    {
        var inner = new FakePluginSessionDriver { Capabilities = _SandboxConfining() };
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);

        // A sandbox provider's confinement does not depend on a resolved permission mode, so it holds from construction.
        Assert.True(adapter.Capabilities.ConfinesFileAccessToWorkingDirectory);

        await adapter.StartAsync(permissionMode: "acceptEdits");
        Assert.True(adapter.Capabilities.ConfinesFileAccessToWorkingDirectory);
    }

    [Fact]
    public async Task Capabilities_ForAPermissionBasedConfiningProvider_RecomputesConfinement_OnALivePermissionModeSwitch()
    {
        var inner = new FakePluginSessionDriver { Capabilities = _PermissionConfining() };
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);

        // Started in a permission-engaged mode → confined.
        await adapter.StartAsync(permissionMode: "acceptEdits");
        Assert.True(adapter.Capabilities.ConfinesFileAccessToWorkingDirectory);

        // AC-190 defense-in-depth: a live switch to a bypass mode disables the guard the confinement leans on, so the
        // capability must not stay a stale "confined". Proven red before the recompute in SetPermissionModeAsync — it
        // kept the start-time value, so a session that went bypass live still vouched confinement.
        await adapter.SetPermissionModeAsync("bypassPermissions");
        Assert.False(adapter.Capabilities.ConfinesFileAccessToWorkingDirectory);

        // And a switch back to a permission-engaged mode re-engages it.
        await adapter.SetPermissionModeAsync("plan");
        Assert.True(adapter.Capabilities.ConfinesFileAccessToWorkingDirectory);
    }

    [Fact]
    public async Task StartAsync_ExcludesLocalOnlyAndTheReservedPermissionServer_FromTheFanOut()
    {
        var inner = new FakePluginSessionDriver();
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersForProjectAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new List<McpServerConfig>
        {
            new() { Name = "cockpit-orchestrator", Transport = McpTransport.Http, Url = "http://127.0.0.1:8765/mcp" },
            new() { Name = "filesystem", Transport = McpTransport.Http, Url = "http://127.0.0.1:1/mcp", Scope = McpServerScope.LocalOnly },
            new() { Name = McpConfigFile.ServerName, Transport = McpTransport.Http, Url = "http://127.0.0.1:2/mcp" },
        });
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey, catalog);

        // No per-session selection — every eligible server, but a local-model-only server and the reserved
        // permission-server key (Codex prompts for approvals itself) must never fan out to the agent.
        await adapter.StartAsync();

        Assert.Equal("cockpit-orchestrator", Assert.Single(inner.LastMcpServers!).Name);
    }

    // AC-378, criterion 3 — the finding this ticket exists for: narrowing a delegated task DOWN to a server the
    // profile advertises but cannot actually mount must never resolve to MORE servers than not narrowing at all.
    // Proven here at the resolution layer (the empty-resolution trap is closed one layer up, in the strict
    // --mcp-config wiring the SDK route now always writes explicitly).
    [Fact]
    public async Task StartAsync_NarrowingToAnAdvertisedButUnmountableServer_NeverResolvesMoreServersThanUnnarrowed()
    {
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersForProjectAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new List<McpServerConfig>
        {
            new() { Name = "youtrack", Transport = McpTransport.Http, Url = "http://example/mcp" },
            new() { Name = "SQL Explorer", Transport = McpTransport.Http, Url = null },
        });

        var unnarrowedInner = new FakePluginSessionDriver();
        var unnarrowedAdapter = new PluginSessionDriverAdapter(unnarrowedInner, unnarrowedInner.Capabilities, _authKey, catalog);
        await unnarrowedAdapter.StartAsync();

        var narrowedInner = new FakePluginSessionDriver();
        var narrowedAdapter = new PluginSessionDriverAdapter(narrowedInner, narrowedInner.Capabilities, _authKey, catalog);
        await narrowedAdapter.StartAsync(enabledMcpServerNames: new HashSet<string> { "SQL Explorer" });

        // Narrowing to only the unmountable server resolves to nothing at this layer — never to more than the
        // unnarrowed baseline's one real server, and the strict headless wiring (ClaudeSdkArguments/
        // ClaudeSdkSessionDriver) is what keeps an empty resolution from then being read by the CLI as "no
        // restriction, use your own config" and silently inheriting more than the baseline.
        Assert.Empty(narrowedInner.LastMcpServers!);
        Assert.True(narrowedInner.LastMcpServers!.Count <= unnarrowedInner.LastMcpServers!.Count);
    }

    [Fact]
    public async Task RespondToPermissionAsync_CarriesTheOperatorsAnswers_ToTheInnerDriver()
    {
        // AC-715: a clarifying question is answered, not merely allowed — the answers must survive the hop across
        // the plugin boundary, or the agent is approved and still waiting.
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);

        await adapter.RespondToPermissionAsync("tool_1", allow: true, """{"Which suites?":"Core"}""", CancellationToken.None);

        Assert.Equal(("tool_1", true), inner.LastPermissionResponse);
        Assert.Equal("""{"Which suites?":"Core"}""", inner.LastPermissionAnswersJson);
    }

    [Fact]
    public async Task AllowPermissionAlwaysAsync_ForwardsTheAlwaysAllowIntent_ToTheInnerDriver()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);

        // D4: the adapter forwards a wildcard always-allow to the plugin driver (a driver that can persist it for the
        // session does; one that cannot falls back to a one-time allow) rather than always approving once itself.
        // AC-1476: an exact one stays in the adapter, since a provider's own always-allow is wider than that.
        await adapter.AllowPermissionAlwaysAsync("tool_1", "read_file", "{}", PermissionRuleScope.Wildcard);

        Assert.Equal("tool_1", inner.LastAllowAlwaysToolUseId);
    }

    [Theory]
    [MemberData(nameof(_EventMappings))]
    public async Task Events_MapsEachPluginEventSubtype_ToItsClaudeSessionEventCounterpart(
        PluginSessionEvent pluginEvent, Func<SessionEvent, bool> isExpectedMapping)
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);

        inner.Emit(pluginEvent);
        inner.Complete();

        var mapped = new List<SessionEvent>();
        await foreach (var evt in adapter.Events)
        {
            mapped.Add(evt);
        }

        Assert.True(isExpectedMapping(Assert.Single(mapped)));
    }

    public static IEnumerable<object[]> _EventMappings()
    {
        yield return
        [
            new PluginSessionInitialized { SessionId = "s1", Tools = ["read_file"] },
            (Func<SessionEvent, bool>)(evt => evt is SessionInitialized init && init.SessionId == "s1" && init.Tools.Single() == "read_file"),
        ];
        yield return
        [
            new PluginAssistantTextDelta { SessionId = "s1", BlockIndex = 2, Text = "hi" },
            (Func<SessionEvent, bool>)(evt => evt is AssistantTextDelta delta && delta.BlockIndex == 2 && delta.Text == "hi"),
        ];
        yield return
        [
            new PluginToolUseRequested { SessionId = "s1", ToolUseId = "t1", ToolName = "read_file", InputJson = "{}" },
            (Func<SessionEvent, bool>)(evt => evt is ToolUseRequested tool && tool.ToolUseId == "t1" && tool.ToolName == "read_file"),
        ];
        yield return
        [
            new PluginToolResult { SessionId = "s1", ToolUseId = "t1", Content = "ok", IsError = false },
            (Func<SessionEvent, bool>)(evt => evt is ToolResult result && result.Content == "ok" && !result.IsError),
        ];
        yield return
        [
            new PluginPermissionRequested { SessionId = "s1", ToolUseId = "t1", ToolName = "read_file", InputJson = "{}" },
            (Func<SessionEvent, bool>)(evt => evt is PermissionRequested permission && permission.ToolUseId == "t1"),
        ];
        yield return
        [
            new PluginTurnCompleted { SessionId = "s1", Subtype = "success", Result = "done", IsError = false, StopReason = null },
            (Func<SessionEvent, bool>)(evt => evt is TurnCompleted turn && turn.Subtype == "success" && turn.Result == "done" && !turn.IsError),
        ];
        yield return
        [
            new PluginSessionError { SessionId = "s1", Message = "boom" },
            (Func<SessionEvent, bool>)(evt => evt is SessionError error && error.Message == "boom" && error.Kind == SessionErrorKind.Unknown),
        ];
        yield return
        [
            // AC-720: Kind and RetryAfter cross the plugin/host boundary as their own (separately typed) enum.
            new PluginSessionError
            {
                SessionId = "s1",
                Message = "not authenticated",
                Kind = PluginSessionErrorKind.AuthRequired,
                RetryAfter = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            },
            (Func<SessionEvent, bool>)(evt => evt is SessionError error
                && error.Kind == SessionErrorKind.AuthRequired
                && error.RetryAfter == new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
        ];

        // #45 D3 — the richer events a plugin can now express: a reasoning trace, the session's cwd, and a turn's
        // token usage, each mapped to its core counterpart so a plugin session fills the same UI as the CLI.
        yield return
        [
            new PluginAssistantThinkingDelta { SessionId = "s1", BlockIndex = 1, Thinking = "hmm" },
            (Func<SessionEvent, bool>)(evt => evt is AssistantThinkingDelta thinking && thinking.BlockIndex == 1 && thinking.Thinking == "hmm"),
        ];
        yield return
        [
            new PluginSessionInitialized { SessionId = "s1", Tools = [], Cwd = "/work/here" },
            (Func<SessionEvent, bool>)(evt => evt is SessionInitialized init && init.Cwd == "/work/here"),
        ];
        yield return
        [
            new PluginSessionInitialized { SessionId = "s1", Tools = [], Model = "claude-sonnet-4-5-20250929" },
            (Func<SessionEvent, bool>)(evt => evt is SessionInitialized init && init.Model == "claude-sonnet-4-5-20250929"),
        ];
        yield return
        [
            new PluginTurnCompleted { SessionId = "s1", Subtype = "success", Result = null, IsError = false, Usage = new PluginTokenUsage(100, 20, 5, 0), NumTurns = 3 },
            (Func<SessionEvent, bool>)(evt => evt is TurnCompleted turn && turn.Usage == new TokenUsage(100, 20, 5, 0) && turn.NumTurns == 3),
        ];
        // AC-146: ParentToolUseId is carried on the PluginSessionEvent base, so it must reach every SessionEvent
        // subtype's own base property, not just one hand-picked case.
        yield return
        [
            new PluginAssistantTextDelta { SessionId = "s1", BlockIndex = 0, Text = "hi", ParentToolUseId = "toolu_task1" },
            (Func<SessionEvent, bool>)(evt => evt.ParentToolUseId == "toolu_task1"),
        ];
    }
}
