using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Cockpit.Core.Sessions;
using Cockpit.Core.Sessions.Permissions;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;
using FluentAssertions;
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
    public void Capabilities_MapsSupportsToolsAndSupportsPermissionsFromThePluginCapabilities()
    {
        var inner = new FakePluginSessionDriver { Capabilities = new PluginSessionCapabilities(true, false) };
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);

        adapter.Capabilities.Should().Be(new SessionCapabilities(
            SupportsTools: true, SupportsPermissions: false, SupportsLiveModelSwitch: false, SupportsPlanMode: false, SupportsThinking: false,
            SupportsVision: false));
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

        adapter.Capabilities.SupportsVision.Should().BeFalse();
    }

    [Fact]
    public void Capabilities_MapsSupportsVisionFromThePluginCapabilities_WhenTrue()
    {
        var inner = new FakePluginSessionDriver { Capabilities = new PluginSessionCapabilities(true, false, SupportsVision: true) };
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);

        adapter.Capabilities.SupportsVision.Should().BeTrue();
    }

    [Fact]
    public void Capabilities_MapsSupportsEnvVarsFromThePluginCapabilities()
    {
        var inner = new FakePluginSessionDriver { Capabilities = new PluginSessionCapabilities(true, true) { SupportsEnvVars = true } };
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);

        adapter.Capabilities.SupportsEnvVars.Should().BeTrue();
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

        inner.LastEnvironment.Should().Contain("AI_OS_ROOT", "/home/raymond/AI-OS");
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

        inner.LastEnvironment.Should().NotContainKey("ANTHROPIC_API_KEY", "a host-controlled variable never crosses");
    }

    // AC-40: every spawned session carries this run's MCP auth key in its environment, so a cockpit-hosted server's
    // config can reference COCKPIT_MCP_KEY instead of embedding a literal — even a profile with no variables of its own.
    [Fact]
    public async Task StartAsync_AlwaysPassesTheMcpAuthKeyToTheDriver()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);

        await adapter.StartAsync(new SessionProfile("work", new ClaudeConfig("/config/dir")));

        inner.LastEnvironment.Should().Contain(WellKnownSessionEnvironment.CockpitMcpKey, _authKey.Value);
    }

    [Fact]
    public void Capabilities_ReportPermissionModeSwitch_UnsupportedForAPlugin_EvenWhenItDoesApprovals()
    {
        var inner = new FakePluginSessionDriver { Capabilities = new PluginSessionCapabilities(true, true) };
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);

        // #45 D4 inc2: a plugin (Codex) does tool approvals — SupportsPermissions is true — but has no Claude
        // permission-mode vocabulary, so the header's permission-mode dropdown must stay hidden for it (it switches
        // its approval policy through the generic live-control panel instead). Claude alone reports it supported.
        adapter.Capabilities.SupportsPermissions.Should().BeTrue();
        adapter.Capabilities.SupportsPermissionModeSwitch.Should().BeFalse();
        SessionCapabilities.ClaudeCli.SupportsPermissionModeSwitch.Should().BeTrue();
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

        adapter.Capabilities.SupportsLiveModelSwitch.Should().BeTrue();
        adapter.Capabilities.SupportsPermissionModeSwitch.Should().BeTrue();

        await adapter.SetModelAsync("opus");
        await adapter.SetPermissionModeAsync("plan");

        inner.LiveOptionSwitches.Should().Contain(("model", "opus"));
        inner.LiveOptionSwitches.Should().Contain(("permission-mode", "plan"));
    }

    [Fact]
    public void CurrentStatus_IsNull_WhenTheDriverReportsNoStatus()
    {
        var inner = new FakePluginSessionDriver { Status = null };
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);

        adapter.CurrentStatus.Should().BeNull();
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
        status.ContextUsedPercent.Should().Be(25);
        status.RateLimits.Should().Equal(
            new SessionRateWindow("5h", 60, resetShort),
            new SessionRateWindow("wk", 80, resetLong));
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

        adapter.Capabilities.SupportsLiveModelSwitch.Should().BeFalse();
        adapter.Capabilities.SupportsPlanMode.Should().BeFalse();
        adapter.Capabilities.SupportsThinking.Should().BeFalse();
    }

    [Fact]
    public async Task StartAsync_ForwardsTheModel_AndRecordsTheProfile()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);
        var profile = new SessionProfile("gemini", new PluginProviderConfig("gemini-provider.gemini", "{}"));

        await adapter.StartAsync(profile, model: "gemini-2.5-flash");

        inner.Started.Should().BeTrue();
        inner.LastModel.Should().Be("gemini-2.5-flash");
        adapter.Profile.Should().Be(profile);
    }

    [Fact]
    public async Task StartAsync_ForwardsTheWorkingDirectory_AndAByIdResume_ToTheInnerDriver()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);

        await adapter.StartAsync(workingDirectory: "/work/here", resume: SessionResume.BySessionId("thread-7"));

        // #45 D5: the adapter no longer drops the cwd and resume the cockpit already knows.
        inner.LastWorkingDirectory.Should().Be("/work/here");
        inner.LastResumeSessionId.Should().Be("thread-7");
    }

    [Fact]
    public async Task StartAsync_PassesNoResumeId_ForAFreshOrMostRecentSession()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);

        // Only a BySessionId resume crosses the narrow surface; New and MostRecent become no resume id
        // (MostRecent needs a provider-side "list newest" step — increment 2).
        await adapter.StartAsync(resume: SessionResume.MostRecent);

        inner.LastResumeSessionId.Should().BeNull();
    }

    [Fact]
    public async Task StartAsync_ForwardsTheLaunchOptions_ToTheInnerDriver()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);
        var launchOptions = new Dictionary<string, string> { ["sandbox"] = "workspace-write", ["model"] = "o3" };

        // The operator's per-session option answers must reach the plugin driver, not be dropped.
        await adapter.StartAsync(launchOptions: launchOptions);

        inner.LastLaunchOptions.Should().BeSameAs(launchOptions);
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

        inner.LastLaunchOptions.Should().ContainKey(WellKnownPluginSessionOptions.PermissionMode)
            .WhoseValue.Should().Be("bypassPermissions");
        // The existing launch options are preserved alongside it.
        inner.LastLaunchOptions.Should().ContainKey("model").WhoseValue.Should().Be("opus");
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

        inner.LastLaunchOptions.Should().ContainKey(WellKnownPluginSessionOptions.PermissionMode)
            .WhoseValue.Should().Be("default");
    }

    [Fact]
    public async Task StartAsync_WithNoPermissionMode_LeavesTheLaunchOptionsUntouched()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);
        var launchOptions = new Dictionary<string, string> { ["sandbox"] = "read-only" };

        await adapter.StartAsync(launchOptions: launchOptions);

        // No typed permission mode to fold — the same dictionary passes through, no permission-mode key invented.
        inner.LastLaunchOptions.Should().BeSameAs(launchOptions);
        inner.LastLaunchOptions.Should().NotContainKey(WellKnownPluginSessionOptions.PermissionMode);
    }

    [Fact]
    public async Task StartAsync_ResolvesTheSelectedRegistryServers_ToTheInnerDriver_MappingTheApiKeyToABearerToken()
    {
        var inner = new FakePluginSessionDriver();
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersAsync(Arg.Any<CancellationToken>()).Returns(new List<McpServerConfig>
        {
            new() { Name = "cockpit-orchestrator", Transport = McpTransport.Http, Url = "http://127.0.0.1:8765/mcp" },
            new() { Name = "youtrack", Transport = McpTransport.Http, Url = "http://127.0.0.1:9000/mcp", Auth = McpServerAuth.ApiKey, ApiKey = "yt-pat-value" },
        });
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey, catalog);

        await adapter.StartAsync(enabledMcpServerNames: new HashSet<string> { "cockpit-orchestrator", "youtrack" });

        inner.LastMcpServers.Should().SatisfyRespectively(
            orchestrator =>
            {
                orchestrator.Name.Should().Be("cockpit-orchestrator");
                orchestrator.Url.Should().Be("http://127.0.0.1:8765/mcp");
                orchestrator.BearerToken.Should().BeNull();
            },
            youtrack =>
            {
                youtrack.Name.Should().Be("youtrack");
                youtrack.BearerToken.Should().Be("yt-pat-value");
            });
    }

    [Fact]
    public async Task StartAsync_ExcludesLocalOnlyAndTheReservedPermissionServer_FromTheFanOut()
    {
        var inner = new FakePluginSessionDriver();
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersAsync(Arg.Any<CancellationToken>()).Returns(new List<McpServerConfig>
        {
            new() { Name = "cockpit-orchestrator", Transport = McpTransport.Http, Url = "http://127.0.0.1:8765/mcp" },
            new() { Name = "filesystem", Transport = McpTransport.Http, Url = "http://127.0.0.1:1/mcp", Scope = McpServerScope.LocalOnly },
            new() { Name = McpConfigFile.ServerName, Transport = McpTransport.Http, Url = "http://127.0.0.1:2/mcp" },
        });
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey, catalog);

        // No per-session selection — every eligible server, but a local-model-only server and the reserved
        // permission-server key (Codex prompts for approvals itself) must never fan out to the agent.
        await adapter.StartAsync();

        inner.LastMcpServers.Should().ContainSingle().Which.Name.Should().Be("cockpit-orchestrator");
    }

    [Fact]
    public async Task StartAsync_HonoursThePerSessionSelection_WhenOneWasMade()
    {
        var inner = new FakePluginSessionDriver();
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersAsync(Arg.Any<CancellationToken>()).Returns(new List<McpServerConfig>
        {
            new() { Name = "a", Transport = McpTransport.Http, Url = "http://a/mcp" },
            new() { Name = "b", Transport = McpTransport.Http, Url = "http://b/mcp" },
        });
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey, catalog);

        await adapter.StartAsync(enabledMcpServerNames: new HashSet<string> { "a" });

        inner.LastMcpServers.Should().ContainSingle().Which.Name.Should().Be("a");
    }

    [Fact]
    public async Task StartAsync_WhenTheRegistryReadFails_StartsWithoutMcpServers_RatherThanFailingTheWholeSession()
    {
        var inner = new FakePluginSessionDriver();
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<IReadOnlyList<McpServerConfig>>(new InvalidOperationException("cockpit.json is locked")));
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey, catalog);

        // A transient registry read failure must degrade to no fan-out (matching the Claude path), never take
        // the whole session start down with it.
        var act = async () => await adapter.StartAsync(enabledMcpServerNames: new HashSet<string> { "youtrack" });

        await act.Should().NotThrowAsync();
        inner.Started.Should().BeTrue();
        inner.LastMcpServers.Should().BeEmpty();
    }

    [Fact]
    public async Task StartAsync_WithNoRegistryStore_PassesNoMcpServers()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);

        await adapter.StartAsync(enabledMcpServerNames: new HashSet<string> { "anything" });

        inner.LastMcpServers.Should().BeEmpty();
    }

    [Fact]
    public async Task SendUserMessageAsync_ForwardsTheText()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);

        await adapter.SendUserMessageAsync("hello");

        inner.SentMessages.Should().ContainSingle().Which.Should().Be("hello");
    }

    [Fact]
    public async Task InterruptAsync_ForwardsToTheInnerDriver()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);

        await adapter.InterruptAsync();

        inner.Interrupted.Should().BeTrue();
    }

    [Fact]
    public async Task RespondToPermissionAsync_ForwardsToolUseIdAndDecision()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);

        await adapter.RespondToPermissionAsync("tool_1", allow: true);

        inner.LastPermissionResponse.Should().Be(("tool_1", true));
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

        inner.LastAllowAlwaysToolUseId.Should().Be("tool_1");
    }

    [Fact]
    public void ProcessId_ForwardsFromTheInnerDriver()
    {
        var inner = new FakePluginSessionDriver { ProcessId = 5150 };
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);

        // D10: the resource meter measures the plugin driver's process (Codex app-server), not nothing.
        adapter.ProcessId.Should().Be(5150);
    }

    [Fact]
    public async Task SetAutoApproveToolsAsync_ForwardsToTheInnerDriver()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);

        await adapter.SetAutoApproveToolsAsync(true);

        inner.LastAutoApprove.Should().BeTrue();
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

        await act.Should().NotThrowAsync();
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
        adapter.LiveOptions.Should().HaveCount(2);

        adapter.LiveOptions[0].Key.Should().Be("model");
        adapter.LiveOptions[0].Label.Should().Be("Model");
        adapter.LiveOptions[0].Choices.Should().Equal("gpt-5-codex", "gpt-5");
        adapter.LiveOptions[0].CurrentValue.Should().Be("gpt-5-codex");

        adapter.LiveOptions[1].Key.Should().Be("effort");
        adapter.LiveOptions[1].Choices.Should().Equal("low", "medium", "high");
        adapter.LiveOptions[1].CurrentValue.Should().BeNull();
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
        adapter.LiveOptions[0].ChoiceLabels.Should().NotBeNull();
        adapter.LiveOptions[0].ChoiceLabels!["default"].Should().Be("Ask permissions");
        adapter.LiveOptions[0].ChoiceLabels!["acceptEdits"].Should().Be("Accept edits");
        adapter.LiveOptions[0].Choices.Should().Equal("default", "acceptEdits");
    }

    [Fact]
    public async Task SetLiveOptionAsync_ForwardsKeyAndValue_ToTheInnerDriver()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);

        await adapter.SetLiveOptionAsync("model", "gpt-5");

        inner.LastLiveOption.Should().Be(("model", "gpt-5"));
    }

    [Fact]
    public async Task DisposeAsync_DisposesTheInnerDriver()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, _authKey);

        await adapter.DisposeAsync();

        inner.Disposed.Should().BeTrue();
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

        mapped.Should().ContainSingle().Which.Should().Match(evt => isExpectedMapping((SessionEvent)evt));
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
            (Func<SessionEvent, bool>)(evt => evt is SessionError error && error.Message == "boom"),
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
            new PluginTurnCompleted { SessionId = "s1", Subtype = "success", Result = null, IsError = false, Usage = new PluginTokenUsage(100, 20, 5, 0), NumTurns = 3 },
            (Func<SessionEvent, bool>)(evt => evt is TurnCompleted turn && turn.Usage == new TokenUsage(100, 20, 5, 0) && turn.NumTurns == 3),
        ];
    }
}
