using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Mcp;
using Cockpit.Core.Sessions;
using Cockpit.Core.Sessions.Permissions;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using System.Reflection;
using System.Threading.Channels;

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
    public async Task EventPump_WaitsForTheBoundedChannelBeforeReadingAnotherPluginEvent()
    {
        var produced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var channel = Channel.CreateBounded<PluginSessionEvent>(4096);
        var pump = typeof(PluginSessionDriverAdapter).GetMethod("_PumpAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        var task = (Task)pump.Invoke(null, [_Events(produced), channel.Writer, CancellationToken.None])!;

        await Task.Delay(50);
        Assert.False(produced.Task.IsCompleted);

        Assert.True(channel.Reader.TryRead(out _));
        await produced.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await task;
    }

    private static async IAsyncEnumerable<PluginSessionEvent> _Events(TaskCompletionSource produced)
    {
        for (var index = 0; index <= 4096; index++)
        {
            yield return null!;
        }

        produced.TrySetResult();
        await Task.CompletedTask;
    }

    [Fact]
    public void Capabilities_MapsSupportsToolsAndSupportsPermissionsFromThePluginCapabilities()
    {
        var inner = new FakePluginSessionDriver { Capabilities = new PluginSessionCapabilities(true, false) };
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);

        Assert.Equal(new SessionCapabilities(
            SupportsTools: true, SupportsPermissions: false, SupportsLiveModelSwitch: false, SupportsPlanMode: false, SupportsThinking: false,
            SupportsVision: false), adapter.Capabilities);
    }

    /// <summary>
    /// SupportsVision (#64) is mapped straight through from the plugin's own capabilities rather than forced
    /// false like the three live-control flags — no example plugin sets it true today (fase 2 not built
    /// yet), but the adapter itself must not be the thing standing in the way once one does.
    /// </summary>
    [Fact]
    public void Capabilities_MapsSupportsVisionFromThePluginCapabilities_WhenFalse()
    {
        var inner = new FakePluginSessionDriver { Capabilities = new PluginSessionCapabilities(true, false, SupportsVision: false) };
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);

        Assert.False(adapter.Capabilities.SupportsVision);
    }

    [Fact]
    public void Capabilities_MapsSupportsVisionFromThePluginCapabilities_WhenTrue()
    {
        var inner = new FakePluginSessionDriver { Capabilities = new PluginSessionCapabilities(true, false, SupportsVision: true) };
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);

        Assert.True(adapter.Capabilities.SupportsVision);
    }

    [Fact]
    public void Capabilities_MapsSupportsEnvVarsFromThePluginCapabilities()
    {
        var inner = new FakePluginSessionDriver { Capabilities = new PluginSessionCapabilities(true, true) { SupportsEnvVars = true } };
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);

        Assert.True(adapter.Capabilities.SupportsEnvVars);
    }

    // The profile's environment variables (AC-22) cross the plugin boundary host-scrubbed: a host-controlled
    // key (an ANTHROPIC_* credential) is dropped here, so no plugin has to be trusted to apply that rule. The
    // MCP auth key (AC-40) always rides along besides them.
    [Fact]
    public async Task StartAsync_PassesTheProfilesEnvironmentVariablesToTheDriver()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);
        var profile = new SessionProfile("work", new ClaudeConfig("/config/dir"))
        {
            EnvironmentVariables = [new ProfileEnvironmentVariable("AI_OS_ROOT", "/home/raymond/AI-OS")],
        };

        await adapter.StartAsync(profile);

        Assert.Contains("AI_OS_ROOT", inner.LastEnvironment!);
    }

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

    // AC-165: what a plugin gives this session reaches the driver the same way a profile's variables do, so a
    // provider needs to know nothing about where a variable came from.
    [Fact]
    public async Task StartAsync_PassesAPluginsContributedVariablesToTheDriver()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(
            inner, inner.Capabilities, _authKey,
            sessionResources: StubResources(("GH_REPO", "raymondkrahwinkel/AI-Cockpit")));

        await adapter.StartAsync(new SessionProfile("work", new ClaudeConfig("/config/dir")), launchOptions: PaneOptions);

        Assert.Contains("GH_REPO", inner.LastEnvironment!);
    }

    // A contribution is the project's answer and a profile variable is the operator's default for every project, so
    // the project wins — the precedence SessionStartDefaults already applies wherever the two answer the same question.
    [Fact]
    public async Task StartAsync_AContributedVariable_BeatsTheProfilesOwn()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(
            inner, inner.Capabilities, _authKey,
            sessionResources: StubResources(("GH_REPO", "from/project")));
        var profile = new SessionProfile("work", new ClaudeConfig("/config/dir"))
        {
            EnvironmentVariables = [new ProfileEnvironmentVariable("GH_REPO", "from/profile")],
        };

        await adapter.StartAsync(profile, launchOptions: PaneOptions);

        Assert.Contains("GH_REPO", inner.LastEnvironment!);
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
    [Fact]
    public async Task StartAsync_WithNoResolverWired_StillPassesTheProfilesVariables()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);
        var profile = new SessionProfile("work", new ClaudeConfig("/config/dir"))
        {
            EnvironmentVariables = [new ProfileEnvironmentVariable("AI_OS_ROOT", "/home/raymond/AI-OS")],
        };

        await adapter.StartAsync(profile, launchOptions: PaneOptions);

        Assert.Contains("AI_OS_ROOT", inner.LastEnvironment!);
    }

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

    [Fact]
    public void Capabilities_ReportPermissionModeSwitch_UnsupportedForAPlugin_EvenWhenItDoesApprovals()
    {
        var inner = new FakePluginSessionDriver { Capabilities = new PluginSessionCapabilities(true, true) };
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);

        // #45 D4 inc2: a plugin (Codex) does tool approvals — SupportsPermissions is true — but has no Claude
        // permission-mode vocabulary, so the header's permission-mode dropdown must stay hidden for it (it switches
        // its approval policy through the generic live-control panel instead). Claude alone reports it supported.
        Assert.True(adapter.Capabilities.SupportsPermissions);
        Assert.False(adapter.Capabilities.SupportsPermissionModeSwitch);
        Assert.True(SessionCapabilities.ClaudeCli.SupportsPermissionModeSwitch);
    }

    [Fact]
    public async Task Capabilities_MapLiveModelAndPermissionModeSwitch_WhenThePluginDeclaresThem_AndWireTheSetters()
    {
        // Fase 4 D4: a plugin that can switch model/permission-mode live (the Claude provider, via SetLiveOptionAsync)
        // declares it, and the adapter maps the flags through AND routes the host's native SetModelAsync/
        // SetPermissionModeAsync to the plugin's live-option surface — proven red before the wiring (both were no-ops).
        var inner = new FakePluginSessionDriver
        {
            Capabilities = new PluginSessionCapabilities(SupportsTools: true, SupportsPermissions: true)
            {
                SupportsLiveModelSwitch = true,
                SupportsPermissionModeSwitch = true,
            },
        };
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);

        Assert.True(adapter.Capabilities.SupportsLiveModelSwitch);
        Assert.True(adapter.Capabilities.SupportsPermissionModeSwitch);

        await adapter.SetModelAsync("opus");
        await adapter.SetPermissionModeAsync("plan");

        Assert.Contains(("model", "opus"), inner.LiveOptionSwitches);
        Assert.Contains(("permission-mode", "plan"), inner.LiveOptionSwitches);
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
    public void CurrentStatus_IsNull_WhenTheDriverReportsNoStatus()
    {
        var inner = new FakePluginSessionDriver { Status = null };
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);

        Assert.Null(adapter.CurrentStatus);
    }

    /// <summary>
    /// The adapter carries the provider's status through to the core model unchanged: context percent, and each
    /// window with the label the provider chose, in the order it reported them — no host-side slotting or window
    /// vocabulary.
    /// </summary>
    [Fact]
    public void CurrentStatus_MapsContextAndWindows_PreservingEachWindowsLabelAndOrder()
    {
        var resetShort = DateTimeOffset.FromUnixTimeSeconds(1800000000);
        var resetLong = DateTimeOffset.FromUnixTimeSeconds(1800600000);
        var inner = new FakePluginSessionDriver
        {
            Status = new PluginSessionStatus(
                ContextUsedPercent: 25,
                RateLimits:
                [
                    new PluginRateLimitWindow("5h", 60, resetShort, 300),
                    new PluginRateLimitWindow("wk", 80, resetLong, 10080),
                ]),
        };
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);

        var status = adapter.CurrentStatus!;
        Assert.Equal(25, status.ContextUsedPercent);
        Assert.Equal(
            new[] { new SessionRateWindow("5h", 60, resetShort), new SessionRateWindow("wk", 80, resetLong) },
            status.RateLimits);
    }

    /// <summary>
    /// Live model switch / plan mode / thinking budget have no member on <see cref="IPluginSessionDriver"/>
    /// that could back them (#45 review finding 3) — the adapter reports them unsupported unconditionally,
    /// not merely mirroring whatever a plugin happens to set on its own <see cref="PluginSessionCapabilities"/>.
    /// </summary>
    [Fact]
    public void Capabilities_AlwaysReportsLiveModelSwitchPlanModeAndThinkingAsUnsupported()
    {
        var inner = new FakePluginSessionDriver { Capabilities = new PluginSessionCapabilities(true, true) };
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);

        Assert.False(adapter.Capabilities.SupportsLiveModelSwitch);
        Assert.False(adapter.Capabilities.SupportsPlanMode);
        Assert.False(adapter.Capabilities.SupportsThinking);
    }

    [Fact]
    public async Task StartAsync_ForwardsTheModel_AndRecordsTheProfile()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);
        var profile = new SessionProfile("gemini", new PluginProviderConfig("gemini-provider.gemini", "{}"));

        await adapter.StartAsync(profile, model: "gemini-2.5-flash");

        Assert.True(inner.Started);
        Assert.Equal("gemini-2.5-flash", inner.LastModel);
        Assert.Equal(profile, adapter.Profile);
    }

    [Fact]
    public async Task StartAsync_ForwardsTheWorkingDirectory_AndAByIdResume_ToTheInnerDriver()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);

        await adapter.StartAsync(workingDirectory: "/work/here", resume: SessionResume.BySessionId("thread-7"));

        // #45 D5: the adapter no longer drops the cwd and resume the cockpit already knows.
        Assert.Equal("/work/here", inner.LastWorkingDirectory);
        Assert.Equal("thread-7", inner.LastResumeSessionId);
    }

    [Fact]
    public async Task StartAsync_PassesNoResumeId_ForAFreshOrMostRecentSession()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);

        // Only a BySessionId resume crosses the narrow surface; New and MostRecent become no resume id
        // (MostRecent needs a provider-side "list newest" step — increment 2).
        await adapter.StartAsync(resume: SessionResume.MostRecent);

        Assert.Null(inner.LastResumeSessionId);
    }

    [Fact]
    public async Task StartAsync_ForwardsTheLaunchOptions_ToTheInnerDriver()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);
        var launchOptions = new Dictionary<string, string> { ["sandbox"] = "workspace-write", ["model"] = "o3" };

        // The operator's per-session option answers must reach the plugin driver, not be dropped. Compared by content
        // rather than by reference: the adapter states this session's attendance (AC-378) on the way through, so what
        // the driver receives is a superset of what the caller passed, never the same instance.
        await adapter.StartAsync(launchOptions: launchOptions);

        Assert.NotNull(inner.LastLaunchOptions);
        Assert.Equal("workspace-write", inner.LastLaunchOptions!["sandbox"]);
        Assert.Equal("o3", inner.LastLaunchOptions["model"]);
    }

    [Fact]
    public async Task StartAsync_StatesThisSessionIsAttended_WhenTheCallerDidNotSayOtherwise()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);

        // AC-378: everything that is not a delegated task or a self-driving embedded run is a pane an operator opened,
        // and the host says so out loud rather than leaving the driver to read absence. A driver must be free to treat
        // silence as unattended (the safe answer on an older host), which only works if a newer host is never silent.
        await adapter.StartAsync(launchOptions: new Dictionary<string, string> { ["model"] = "opus" });

        Assert.NotNull(inner.LastLaunchOptions);
        Assert.Equal("false", inner.LastLaunchOptions![WellKnownPluginSessionOptions.Unattended]);
    }

    [Fact]
    public async Task StartAsync_LeavesAnExplicitUnattendedMarkerAlone()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);

        // DelegationService and an Autopilot step say "nobody is watching"; the adapter's default must not overwrite
        // that, or every delegated task would present itself as attended and its narrowing would stop binding.
        await adapter.StartAsync(launchOptions: new Dictionary<string, string>
        {
            [WellKnownPluginSessionOptions.Unattended] = "true",
        });

        Assert.NotNull(inner.LastLaunchOptions);
        Assert.Equal("true", inner.LastLaunchOptions![WellKnownPluginSessionOptions.Unattended]);
    }

    [Fact]
    public async Task StartAsync_FoldsTheTypedPermissionMode_IntoTheInnerDriversOptions()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);

        // The host carries the operator's permission-mode selection as a typed parameter; it must reach a plugin that
        // declared a permission-mode option, or a launch-time "bypassPermissions" silently becomes the driver default.
        // Proven red before _MergePermissionMode: the adapter dropped the typed permissionMode entirely.
        await adapter.StartAsync(permissionMode: "bypassPermissions", launchOptions: new Dictionary<string, string> { ["model"] = "opus" });

        var lastLaunchOptions = inner.LastLaunchOptions;
        Assert.NotNull(lastLaunchOptions);
        Assert.True(lastLaunchOptions.ContainsKey(WellKnownPluginSessionOptions.PermissionMode));
        Assert.Equal("bypassPermissions", lastLaunchOptions[WellKnownPluginSessionOptions.PermissionMode]);
        // The existing launch options are preserved alongside it.
        Assert.True(lastLaunchOptions.ContainsKey("model"));
        Assert.Equal("opus", lastLaunchOptions["model"]);
    }

    [Fact]
    public async Task StartAsync_WhenTheLaunchOptionsAlreadyCarryAPermissionMode_TheOperatorsExplicitChoiceWins_OverTheTypedFold()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);

        // The operator picked "default" (Ask permissions) in the provider's own permission-mode option; a profile's
        // stale typed default (bypass) must not fold over it, or a write tool runs ungated. Proven red before the guard:
        // the typed value overwrote the explicit launch-time choice, so a session started as bypass.
        await adapter.StartAsync(
            permissionMode: "bypassPermissions",
            launchOptions: new Dictionary<string, string> { [WellKnownPluginSessionOptions.PermissionMode] = "default", ["model"] = "opus" });

        var lastLaunchOptions = inner.LastLaunchOptions;
        Assert.NotNull(lastLaunchOptions);
        Assert.True(lastLaunchOptions.ContainsKey(WellKnownPluginSessionOptions.PermissionMode));
        Assert.Equal("default", lastLaunchOptions[WellKnownPluginSessionOptions.PermissionMode]);
    }

    [Fact]
    public async Task StartAsync_WithNoPermissionMode_LeavesTheLaunchOptionsUntouched()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);
        var launchOptions = new Dictionary<string, string> { ["sandbox"] = "read-only" };

        await adapter.StartAsync(launchOptions: launchOptions);

        // No typed permission mode to fold — the caller's own entries pass through and no permission-mode key is
        // invented. The attendance marker the adapter states (AC-378) is the one addition, and it says "attended":
        // this launch is nobody's delegated task.
        Assert.NotNull(inner.LastLaunchOptions);
        Assert.Equal("read-only", inner.LastLaunchOptions!["sandbox"]);
        Assert.False(inner.LastLaunchOptions.ContainsKey(WellKnownPluginSessionOptions.PermissionMode));
        Assert.Equal("false", inner.LastLaunchOptions[WellKnownPluginSessionOptions.Unattended]);
    }

    [Fact]
    public async Task StartAsync_ResolvesTheSelectedRegistryServers_ToTheInnerDriver_MappingTheApiKeyToABearerToken()
    {
        var inner = new FakePluginSessionDriver();
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersForProjectAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new List<McpServerConfig>
        {
            new() { Name = "cockpit-orchestrator", Transport = McpTransport.Http, Url = "http://127.0.0.1:8765/mcp" },
            new() { Name = "youtrack", Transport = McpTransport.Http, Url = "http://127.0.0.1:9000/mcp", Auth = McpServerAuth.ApiKey, ApiKey = "yt-pat-value" },
        });
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey, catalog);

        await adapter.StartAsync(enabledMcpServerNames: new HashSet<string> { "cockpit-orchestrator", "youtrack" });

        Assert.Collection(inner.LastMcpServers!,
            orchestrator =>
            {
                Assert.Equal("cockpit-orchestrator", orchestrator.Name);
                Assert.Equal("http://127.0.0.1:8765/mcp", orchestrator.Url);
                Assert.Null(orchestrator.BearerToken);
            },
            youtrack =>
            {
                Assert.Equal("youtrack", youtrack.Name);
                Assert.Equal("yt-pat-value", youtrack.BearerToken);
            });
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

    // AC-378, criterion 6: the registry can advertise a server as agent-eligible (enabled, in scope) that this
    // driver still cannot mount — an Http entry with no Url is the concrete shape a misconfigured "SQL Explorer"
    // takes. That must be logged, not silently dropped, so "why does my session have fewer tools than the profile
    // listing promised" is a log line rather than a bisect.
    [Fact]
    public async Task StartAsync_AnAdvertisedServerWithNoTransportTarget_LogsAWarningNamingIt()
    {
        var inner = new FakePluginSessionDriver();
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersForProjectAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new List<McpServerConfig>
        {
            new() { Name = "SQL Explorer", Transport = McpTransport.Http, Url = null },
        });
        var logger = Substitute.For<ILogger<PluginSessionDriverAdapter>>();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey, catalog, logger);

        await adapter.StartAsync();

        Assert.Empty(inner.LastMcpServers!);
        logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(state => state!.ToString()!.Contains("SQL Explorer")),
            null,
            Arg.Any<Func<object, Exception?, string>>());
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

    // AC-218: the fan-out asks for the servers as the session's project sees them. Asking the unscoped catalog is
    // what made a project's own server and its overrides invisible to a running session while the checklist that
    // offered them was already project-scoped.
    [Fact]
    public async Task StartAsync_ResolvesTheRegistryAsTheProjectSeesIt()
    {
        var inner = new FakePluginSessionDriver();
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersForProjectAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<McpServerConfig>());
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey, catalog);

        await adapter.StartAsync(projectId: "project-1");

        await catalog.Received().GetServersForProjectAsync("project-1", Arg.Any<CancellationToken>());
    }

    // The point of the ticket: a server the project brings itself (ProjectMcpOverlay.AdditionalServers) exists only
    // in the project-scoped list, so resolving against the unscoped registry dropped it however the operator ticked.
    [Fact]
    public async Task StartAsync_AServerTheProjectBringsItself_ReachesTheSession()
    {
        var inner = new FakePluginSessionDriver();
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersForProjectAsync("project-1", Arg.Any<CancellationToken>()).Returns(new List<McpServerConfig>
        {
            new() { Name = "registry", Transport = McpTransport.Http, Url = "http://registry/mcp" },
            new() { Name = "project-own", Transport = McpTransport.Http, Url = "http://project/mcp" },
        });
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey, catalog);

        await adapter.StartAsync(enabledMcpServerNames: new HashSet<string> { "project-own" }, projectId: "project-1");

        Assert.Equal("project-own", Assert.Single(inner.LastMcpServers!).Name);
    }

    [Fact]
    public async Task StartAsync_HonoursThePerSessionSelection_WhenOneWasMade()
    {
        var inner = new FakePluginSessionDriver();
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersForProjectAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new List<McpServerConfig>
        {
            new() { Name = "a", Transport = McpTransport.Http, Url = "http://a/mcp" },
            new() { Name = "b", Transport = McpTransport.Http, Url = "http://b/mcp" },
        });
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey, catalog);

        await adapter.StartAsync(enabledMcpServerNames: new HashSet<string> { "a" });

        Assert.Equal("a", Assert.Single(inner.LastMcpServers!).Name);
    }

    [Fact]
    public async Task StartAsync_WithNoPerSessionSelection_FallsBackToTheProfilesSavedSelection()
    {
        var inner = new FakePluginSessionDriver();
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersForProjectAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new List<McpServerConfig>
        {
            new() { Name = "a", Transport = McpTransport.Http, Url = "http://a/mcp" },
            new() { Name = "b", Transport = McpTransport.Http, Url = "http://b/mcp" },
        });
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey, catalog);
        var profile = new SessionProfile("dev", new ClaudeConfig("/config/dir")) { EnabledMcpServerNames = ["a"] };

        // #44/AC-130: a programmatic launch (a plugin/workflow shortcut, a restored session) passes no per-session
        // selection, so the profile's saved checklist applies instead of every eligible server. Proven red before
        // EffectiveSessionSelection, when a null selection reached both a and b — the SDK route's half of the gap.
        await adapter.StartAsync(profile);

        Assert.Equal("a", Assert.Single(inner.LastMcpServers!).Name);
    }

    [Fact]
    public async Task StartAsync_WithAnExplicitEmptySelection_StartsWithNoServers_EvenWhenTheProfileHasASavedSelection()
    {
        var inner = new FakePluginSessionDriver();
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersForProjectAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new List<McpServerConfig>
        {
            new() { Name = "a", Transport = McpTransport.Http, Url = "http://a/mcp" },
        });
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey, catalog);
        var profile = new SessionProfile("dev", new ClaudeConfig("/config/dir")) { EnabledMcpServerNames = ["a"] };

        // A deliberate "these none" per-session selection wins over the profile's saved set — it must not fall
        // back to it. Guards against a future "treat empty like null" simplification of EffectiveSessionSelection.
        await adapter.StartAsync(profile, enabledMcpServerNames: new HashSet<string>());

        Assert.Empty(inner.LastMcpServers!);
    }

    [Fact]
    public async Task StartAsync_WhenTheRegistryReadFails_StartsWithoutMcpServers_RatherThanFailingTheWholeSession()
    {
        var inner = new FakePluginSessionDriver();
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersForProjectAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<IReadOnlyList<McpServerConfig>>(new InvalidOperationException("cockpit.json is locked")));
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey, catalog);

        // A transient registry read failure must degrade to no fan-out (matching the Claude path), never take
        // the whole session start down with it.
        var act = async () => await adapter.StartAsync(enabledMcpServerNames: new HashSet<string> { "youtrack" });

        await act();
        Assert.True(inner.Started);
        Assert.Empty(inner.LastMcpServers!);
    }

    [Fact]
    public async Task StartAsync_WithNoRegistryStore_PassesNoMcpServers()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);

        await adapter.StartAsync(enabledMcpServerNames: new HashSet<string> { "anything" });

        Assert.Empty(inner.LastMcpServers!);
    }

    [Fact]
    public async Task SendUserMessageAsync_ForwardsTheText()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);

        await adapter.SendUserMessageAsync("hello");

        Assert.Equal("hello", Assert.Single(inner.SentMessages));
    }

    [Fact]
    public async Task InterruptAsync_ForwardsToTheInnerDriver()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);

        await adapter.InterruptAsync();

        Assert.True(inner.Interrupted);
    }

    [Fact]
    public async Task RespondToPermissionAsync_ForwardsToolUseIdAndDecision()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);

        await adapter.RespondToPermissionAsync("tool_1", allow: true);

        Assert.Equal(("tool_1", true), inner.LastPermissionResponse);
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

        // D4: the adapter forwards the always-allow to the plugin driver (a driver that can persist it for the
        // session does; one that cannot falls back to a one-time allow) rather than always approving once itself.
        // The Claude rule args (toolName/input/scope) have no equivalent on the narrow surface and are dropped.
        await adapter.AllowPermissionAlwaysAsync("tool_1", "read_file", "{}", PermissionRuleScope.Exact);

        Assert.Equal("tool_1", inner.LastAllowAlwaysToolUseId);
    }

    [Fact]
    public void ProcessId_ForwardsFromTheInnerDriver()
    {
        var inner = new FakePluginSessionDriver { ProcessId = 5150 };
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);

        // D10: the resource meter measures the plugin driver's process (Codex app-server), not nothing.
        Assert.Equal(5150, adapter.ProcessId);
    }

    [Fact]
    public async Task SetAutoApproveToolsAsync_ForwardsToTheInnerDriver()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);

        await adapter.SetAutoApproveToolsAsync(true);

        Assert.True(inner.LastAutoApprove);
    }

    [Fact]
    public async Task ClaudeCliOnlyLiveControls_AreNoOps_AndDoNotThrow()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);

        var act = async () =>
        {
            await adapter.SetPermissionModeAsync("plan");
            await adapter.SetModelAsync("some-model");
            await adapter.SetMaxThinkingTokensAsync(1024);
        };

        await act();
    }

    [Fact]
    public void LiveOptions_MapEachPluginOption_ToTheCoreForm_PreservingKeyLabelChoicesAndCurrentValue()
    {
        var inner = new FakePluginSessionDriver
        {
            LiveOptions =
            [
                new PluginSessionLaunchOption("model", "Model", ["gpt-5-codex", "gpt-5"], "gpt-5-codex"),
                new PluginSessionLaunchOption("effort", "Effort", ["low", "medium", "high"]),
            ],
        };
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);

        // D4: the provider's live controls cross the boundary onto the core form the header renders — each option's
        // key, label and choices carried through, and DefaultValue mapped to CurrentValue (unset for effort).
        Assert.Equal(2, System.Linq.Enumerable.Count(adapter.LiveOptions));

        Assert.Equal("model", adapter.LiveOptions[0].Key);
        Assert.Equal("Model", adapter.LiveOptions[0].Label);
        Assert.Equal(new[] { "gpt-5-codex", "gpt-5" }, adapter.LiveOptions[0].Choices);
        Assert.Equal("gpt-5-codex", adapter.LiveOptions[0].CurrentValue);

        Assert.Equal("effort", adapter.LiveOptions[1].Key);
        Assert.Equal(new[] { "low", "medium", "high" }, adapter.LiveOptions[1].Choices);
        Assert.Null(adapter.LiveOptions[1].CurrentValue);
    }

    [Fact]
    public void LiveOptions_CarryTheProviderChoiceLabels_OntoTheCoreForm()
    {
        var inner = new FakePluginSessionDriver
        {
            LiveOptions =
            [
                new PluginSessionLaunchOption("permissionMode", "Permissions", ["default", "acceptEdits"], "default")
                {
                    ChoiceLabels = new Dictionary<string, string> { ["default"] = "Ask permissions", ["acceptEdits"] = "Accept edits" },
                },
            ],
        };
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);

        // Fase 4 step 1: the provider owns the friendly labels; the adapter carries them onto the core form so the
        // header can show "Ask permissions" instead of the raw CLI value "default", while the value still round-trips.
        Assert.NotNull(adapter.LiveOptions[0].ChoiceLabels);
        Assert.Equal("Ask permissions", adapter.LiveOptions[0].ChoiceLabels!["default"]);
        Assert.Equal("Accept edits", adapter.LiveOptions[0].ChoiceLabels!["acceptEdits"]);
        Assert.Equal(new[] { "default", "acceptEdits" }, adapter.LiveOptions[0].Choices);
    }

    [Fact]
    public async Task SetLiveOptionAsync_ForwardsKeyAndValue_ToTheInnerDriver()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);

        await adapter.SetLiveOptionAsync("model", "gpt-5");

        Assert.Equal(("model", "gpt-5"), inner.LastLiveOption);
    }

    [Fact]
    public async Task DisposeAsync_DisposesTheInnerDriver()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);

        await adapter.DisposeAsync();

        Assert.True(inner.Disposed);
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
