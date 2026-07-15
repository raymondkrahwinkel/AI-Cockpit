using Cockpit.Core.Sessions;
using Cockpit.Core.Sessions.Permissions;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;
using FluentAssertions;

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
    public async Task AllowPermissionAlwaysAsync_RespondsAllowOnTheInnerDriver_WithNoRulePersistence()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities);

        await adapter.AllowPermissionAlwaysAsync("tool_1", "read_file", "{}", PermissionRuleScope.Exact);

        inner.LastPermissionResponse.Should().Be(("tool_1", true));
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
