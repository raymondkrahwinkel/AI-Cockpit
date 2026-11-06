using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Mcp;
using Cockpit.Core.Sessions;
using Cockpit.Core.Sessions.Permissions;
using Cockpit.Core.Profiles;
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
public class PluginSessionDriverAdapterTests : SessionMcpTestFixture
{

    [Fact]
    public async Task StartAsync_AProfileVariableOnAHostControlledKey_NeverCrossesThePluginBoundary()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, EmptyCatalog, keyring: Keyring, mcpMounts: Mounts, gateway: Gateway);
        var profile = new SessionProfile("work", new ClaudeConfig("/config/dir"))
        {
            EnvironmentVariables = [new ProfileEnvironmentVariable("ANTHROPIC_API_KEY", "smuggled", IsSecret: true)],
        };

        await adapter.StartAsync(profile);

        Assert.False(inner.LastEnvironment!.ContainsKey("ANTHROPIC_API_KEY"));

        await Gateway.StopAsync(CancellationToken.None);
        var stopped = new FakePluginSessionDriver();
        await using var failed = new PluginSessionDriverAdapter(stopped, stopped.Capabilities, EmptyCatalog, keyring: Keyring, mcpMounts: Mounts, gateway: Gateway);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => failed.StartAsync(profile));
        Assert.Contains("MCP gateway is unavailable", failure.Message);
        Assert.Null(stopped.LastEnvironment);
    }

    // The same rule the profile's variables meet, applied where the value is put in the driver's environment rather
    // than trusted to have been applied upstream — the resolver and the merge both scrub too, and this is what still
    // holds if either of them ever stops. A contribution must not be able to rename the session it is running in
    // either: the pane id is the identity the consent broker and the session-status tool attribute by, so one a
    // plugin chose would let it act as another pane.
    [Fact]
    public async Task StartAsync_AContributedVariableOnAHostControlledKeyOrPaneId_NeverCrossesThePluginBoundary()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(
            inner, inner.Capabilities, EmptyCatalog, keyring: Keyring, mcpMounts: Mounts, gateway: Gateway,
            sessionResources: StubResources(
                ("ANTHROPIC_API_KEY", "smuggled"), ("COCKPIT_PANE_ID", "someone-elses-pane"), ("GH_REPO", "owner/repo")));

        await adapter.StartAsync(new SessionProfile("work", new ClaudeConfig("/config/dir")), launchOptions: PaneOptions);

        Assert.False(inner.LastEnvironment!.ContainsKey("ANTHROPIC_API_KEY"));
        Assert.False(inner.LastEnvironment!.ContainsKey("COCKPIT_PANE_ID"));
        Assert.Contains("GH_REPO", inner.LastEnvironment!);
    }

    /// <summary>
    /// AC-89: the session's MCP identity dies with the session. Without this the token stays a valid bearer for every
    /// cockpit-hosted endpoint until the app restarts, still naming a pane that is gone — and the consent broker keys
    /// remembered approvals on exactly that pane id.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_RevokesTheTokenItMintedForTheSession()
    {
        var keyring = Keyring;
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, EmptyCatalog, keyring: keyring, mcpMounts: Mounts, gateway: Gateway);
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
        var keyring = Keyring;
        var closing = new FakePluginSessionDriver();
        var first = new PluginSessionDriverAdapter(closing, closing.Capabilities, EmptyCatalog, keyring: keyring, mcpMounts: Mounts, gateway: Gateway);
        await first.StartAsync(launchOptions: PaneOptions);
        var second = new FakePluginSessionDriver();
        var restarted = new PluginSessionDriverAdapter(second, second.Capabilities, EmptyCatalog, keyring: keyring, mcpMounts: Mounts, gateway: Gateway);
        await restarted.StartAsync(launchOptions: PaneOptions);
        Assert.NotNull(second.LastEnvironment);
        var live = second.LastEnvironment[WellKnownSessionEnvironment.CockpitMcpKey];

        await first.DisposeAsync();

        Assert.Equal("pane-1", keyring.PaneFor(live));
    }

    private static readonly IReadOnlyDictionary<string, string> PaneOptions =
        new Dictionary<string, string> { [WellKnownPluginSessionOptions.PaneId] = "pane-1" };

    private static ISessionResourceResolver StubResources(params (string Key, string Value)[] variables)
    {
        var resolver = Substitute.For<ISessionResourceResolver>();
        resolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<IReadOnlySet<string>?>(), Arg.Any<CancellationToken>())
            .Returns(new SessionResources(variables.ToDictionary(
                variable => variable.Key, variable => variable.Value, StringComparer.Ordinal)));
        return resolver;
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
    public async Task Capabilities_StartedInBypass_UnconfinesAPermissionBasedProvider_ButNotASandboxedOne()
    {
        var inner = new FakePluginSessionDriver { Capabilities = _PermissionConfining() };
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, EmptyCatalog, keyring: Keyring, mcpMounts: Mounts, gateway: Gateway);

        // bypassPermissions (--dangerously-skip-permissions) disables the permission guard the confinement leans on, so
        // the isolate-in-worktree gate must see this session as NOT confined and refuse it — the AC-190 fail-closed fix.
        // Proven red before the fix: the adapter copied the static "true" registration capability regardless of mode.
        await adapter.StartAsync(permissionMode: "bypassPermissions");

        Assert.False(adapter.Capabilities.ConfinesFileAccessToWorkingDirectory);

        // Codex confines via a real OS sandbox, independent of its permission/approval mode — it must NOT be downgraded
        // by the AC-190 permission-mode check. Regression guard that the fix touches only permission-based providers.
        var sandboxed = new FakePluginSessionDriver { Capabilities = _SandboxConfining() };
        var sandboxedAdapter = new PluginSessionDriverAdapter(sandboxed, sandboxed.Capabilities, EmptyCatalog, keyring: Keyring, mcpMounts: Mounts, gateway: Gateway);
        await sandboxedAdapter.StartAsync(permissionMode: "bypassPermissions");

        Assert.True(sandboxedAdapter.Capabilities.ConfinesFileAccessToWorkingDirectory);
    }

    [Fact]
    public async Task Capabilities_ForAPermissionBasedConfiningProvider_RecomputesConfinement_OnALivePermissionModeSwitch()
    {
        var inner = new FakePluginSessionDriver { Capabilities = _PermissionConfining() };
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, EmptyCatalog, keyring: Keyring, mcpMounts: Mounts, gateway: Gateway);

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
    public async Task StartAsync_ExcludesLocalOnlyAndTheReservedPermissionServer_FromTheGatewaySet()
    {
        var inner = new FakePluginSessionDriver();
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersForProjectAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new List<McpServerConfig>
        {
            new() { Name = "cockpit-orchestrator", Transport = McpTransport.Http, Url = "http://127.0.0.1:8765/mcp" },
            new() { Name = "filesystem", Transport = McpTransport.Http, Url = "http://127.0.0.1:1/mcp", Scope = McpServerScope.LocalOnly },
            new() { Name = McpConfigFile.ServerName, Transport = McpTransport.Http, Url = "http://127.0.0.1:2/mcp" },
        });
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, catalog, keyring: Keyring, mcpMounts: Mounts, gateway: Gateway);

        // No per-session selection — every eligible server, but a local-model-only server and the reserved
        // permission-server key (Codex prompts for approvals itself) must never fan out to the agent.
        await adapter.StartAsync(profile: new SessionProfile("Assistant", new PluginProviderConfig("test", "{}")));

        var mounted = Assert.Single(inner.LastMcpServers ?? []);
        Assert.Equal("cockpit", mounted.Name);
        Assert.Null(mounted.BearerToken);
        Assert.Empty(mounted.Headers);
        var paneId = inner.LastLaunchOptions?.GetValueOrDefault(WellKnownPluginSessionOptions.PaneId) ?? throw new InvalidOperationException("Missing pane.");
        Assert.Equal(["cockpit-orchestrator"], Mounts.Set(paneId)?.ServerNames);
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
        var unnarrowedAdapter = new PluginSessionDriverAdapter(unnarrowedInner, unnarrowedInner.Capabilities, catalog, keyring: Keyring, mcpMounts: Mounts, gateway: Gateway);
        await unnarrowedAdapter.StartAsync();

        var narrowedInner = new FakePluginSessionDriver();
        var narrowedAdapter = new PluginSessionDriverAdapter(narrowedInner, narrowedInner.Capabilities, catalog, keyring: Keyring, mcpMounts: Mounts, gateway: Gateway);
        await narrowedAdapter.StartAsync(enabledMcpServerNames: new HashSet<string> { "SQL Explorer" });

        Assert.Equal("cockpit", Assert.Single(narrowedInner.LastMcpServers ?? []).Name);
        var paneId = narrowedInner.LastLaunchOptions?.GetValueOrDefault(WellKnownPluginSessionOptions.PaneId) ?? throw new InvalidOperationException("Missing pane.");
        Assert.Equal(["SQL Explorer"], Mounts.Set(paneId)?.ServerNames);
    }
}
