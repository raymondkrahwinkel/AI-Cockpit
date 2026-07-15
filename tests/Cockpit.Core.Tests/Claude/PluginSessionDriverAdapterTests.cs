using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Cockpit.Core.Sessions;
using Cockpit.Core.Sessions.Permissions;
using Cockpit.Core.Profiles;
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
    [Fact]
    public void Capabilities_MapsSupportsToolsAndSupportsPermissionsFromThePluginCapabilities()
    {
        var inner = new FakePluginSessionDriver { Capabilities = new PluginSessionCapabilities(true, false) };
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities);

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
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities);

        adapter.Capabilities.SupportsVision.Should().BeFalse();
    }

    [Fact]
    public void Capabilities_MapsSupportsVisionFromThePluginCapabilities_WhenTrue()
    {
        var inner = new FakePluginSessionDriver { Capabilities = new PluginSessionCapabilities(true, false, SupportsVision: true) };
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities);

        adapter.Capabilities.SupportsVision.Should().BeTrue();
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
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities);

        adapter.Capabilities.SupportsLiveModelSwitch.Should().BeFalse();
        adapter.Capabilities.SupportsPlanMode.Should().BeFalse();
        adapter.Capabilities.SupportsThinking.Should().BeFalse();
    }

    [Fact]
    public async Task StartAsync_ForwardsTheModel_AndRecordsTheProfile()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities);
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
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities);

        await adapter.StartAsync(workingDirectory: "/work/here", resume: SessionResume.BySessionId("thread-7"));

        // #45 D5: the adapter no longer drops the cwd and resume the cockpit already knows.
        inner.LastWorkingDirectory.Should().Be("/work/here");
        inner.LastResumeSessionId.Should().Be("thread-7");
    }

    [Fact]
    public async Task StartAsync_PassesNoResumeId_ForAFreshOrMostRecentSession()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities);

        // Only a BySessionId resume crosses the narrow surface; New and MostRecent become no resume id
        // (MostRecent needs a provider-side "list newest" step — increment 2).
        await adapter.StartAsync(resume: SessionResume.MostRecent);

        inner.LastResumeSessionId.Should().BeNull();
    }

    [Fact]
    public async Task StartAsync_ForwardsTheLaunchOptions_ToTheInnerDriver()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities);
        var launchOptions = new Dictionary<string, string> { ["sandbox"] = "workspace-write", ["model"] = "o3" };

        // The operator's per-session option answers must reach the plugin driver, not be dropped.
        await adapter.StartAsync(launchOptions: launchOptions);

        inner.LastLaunchOptions.Should().BeSameAs(launchOptions);
    }

    [Fact]
    public async Task StartAsync_ResolvesTheSelectedRegistryServers_ToTheInnerDriver_MappingTheApiKeyToABearerToken()
    {
        var inner = new FakePluginSessionDriver();
        var store = Substitute.For<IMcpServerStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new List<McpServerConfig>
        {
            new() { Name = "cockpit-orchestrator", Transport = McpTransport.Http, Url = "http://127.0.0.1:8765/mcp" },
            new() { Name = "youtrack", Transport = McpTransport.Http, Url = "http://127.0.0.1:9000/mcp", Auth = McpServerAuth.ApiKey, ApiKey = "yt-pat-value" },
        });
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, store);

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
        var store = Substitute.For<IMcpServerStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new List<McpServerConfig>
        {
            new() { Name = "cockpit-orchestrator", Transport = McpTransport.Http, Url = "http://127.0.0.1:8765/mcp" },
            new() { Name = "filesystem", Transport = McpTransport.Http, Url = "http://127.0.0.1:1/mcp", Scope = McpServerScope.LocalOnly },
            new() { Name = McpConfigFile.ServerName, Transport = McpTransport.Http, Url = "http://127.0.0.1:2/mcp" },
        });
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, store);

        // No per-session selection — every eligible server, but a local-model-only server and the reserved
        // permission-server key (Codex prompts for approvals itself) must never fan out to the agent.
        await adapter.StartAsync();

        inner.LastMcpServers.Should().ContainSingle().Which.Name.Should().Be("cockpit-orchestrator");
    }

    [Fact]
    public async Task StartAsync_HonoursThePerSessionSelection_WhenOneWasMade()
    {
        var inner = new FakePluginSessionDriver();
        var store = Substitute.For<IMcpServerStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new List<McpServerConfig>
        {
            new() { Name = "a", Transport = McpTransport.Http, Url = "http://a/mcp" },
            new() { Name = "b", Transport = McpTransport.Http, Url = "http://b/mcp" },
        });
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, store);

        await adapter.StartAsync(enabledMcpServerNames: new HashSet<string> { "a" });

        inner.LastMcpServers.Should().ContainSingle().Which.Name.Should().Be("a");
    }

    [Fact]
    public async Task StartAsync_WhenTheRegistryReadFails_StartsWithoutMcpServers_RatherThanFailingTheWholeSession()
    {
        var inner = new FakePluginSessionDriver();
        var store = Substitute.For<IMcpServerStore>();
        store.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<IReadOnlyList<McpServerConfig>>(new InvalidOperationException("cockpit.json is locked")));
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, store);

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
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities);

        await adapter.StartAsync(enabledMcpServerNames: new HashSet<string> { "anything" });

        inner.LastMcpServers.Should().BeEmpty();
    }

    [Fact]
    public async Task SendUserMessageAsync_ForwardsTheText()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities);

        await adapter.SendUserMessageAsync("hello");

        inner.SentMessages.Should().ContainSingle().Which.Should().Be("hello");
    }

    [Fact]
    public async Task InterruptAsync_ForwardsToTheInnerDriver()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities);

        await adapter.InterruptAsync();

        inner.Interrupted.Should().BeTrue();
    }

    [Fact]
    public async Task RespondToPermissionAsync_ForwardsToolUseIdAndDecision()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities);

        await adapter.RespondToPermissionAsync("tool_1", allow: true);

        inner.LastPermissionResponse.Should().Be(("tool_1", true));
    }

    [Fact]
    public async Task AllowPermissionAlwaysAsync_ForwardsTheAlwaysAllowIntent_ToTheInnerDriver()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities);

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
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities);

        // D10: the resource meter measures the plugin driver's process (Codex app-server), not nothing.
        adapter.ProcessId.Should().Be(5150);
    }

    [Fact]
    public async Task SetAutoApproveToolsAsync_ForwardsToTheInnerDriver()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities);

        await adapter.SetAutoApproveToolsAsync(true);

        inner.LastAutoApprove.Should().BeTrue();
    }

    [Fact]
    public async Task ClaudeCliOnlyLiveControls_AreNoOps_AndDoNotThrow()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities);

        var act = async () =>
        {
            await adapter.SetPermissionModeAsync("plan");
            await adapter.SetModelAsync("some-model");
            await adapter.SetMaxThinkingTokensAsync(1024);
        };

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DisposeAsync_DisposesTheInnerDriver()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities);

        await adapter.DisposeAsync();

        inner.Disposed.Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(_EventMappings))]
    public async Task Events_MapsEachPluginEventSubtype_ToItsClaudeSessionEventCounterpart(
        PluginSessionEvent pluginEvent, Func<SessionEvent, bool> isExpectedMapping)
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities);

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
    }
}
