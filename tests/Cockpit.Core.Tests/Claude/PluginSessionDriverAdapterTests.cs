using Cockpit.Core.Claude;
using Cockpit.Core.Claude.Permissions;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Claude;
using Cockpit.Plugins.Abstractions.Sessions;
using FluentAssertions;

namespace Cockpit.Core.Tests.Claude;

/// <summary>
/// <see cref="PluginSessionDriverAdapter"/> (#45): wraps a <see cref="FakePluginSessionDriver"/> and proves
/// it satisfies <c>ISessionDriver</c> by forwarding every real member and mapping each
/// <see cref="PluginSessionEvent"/> subtype to its <see cref="ClaudeSessionEvent"/> counterpart. The
/// Claude-CLI-only live-control members (permission mode / model / thinking budget) have no equivalent on
/// the narrow interface and must be safe no-ops rather than throwing.
/// </summary>
public class PluginSessionDriverAdapterTests
{
    [Fact]
    public void Capabilities_MapsEveryFieldFromThePluginCapabilities()
    {
        var inner = new FakePluginSessionDriver { Capabilities = new PluginSessionCapabilities(true, false, true, false, true) };
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities);

        adapter.Capabilities.Should().Be(new SessionCapabilities(
            SupportsTools: true, SupportsPermissions: false, SupportsLiveModelSwitch: true, SupportsPlanMode: false, SupportsThinking: true));
    }

    [Fact]
    public async Task StartAsync_ForwardsTheModel_AndRecordsTheProfile()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities);
        var profile = new ClaudeProfile("gemini", ConfigDir: "", ProviderConfig: new PluginProviderConfig("gemini-provider.gemini", "{}"));

        await adapter.StartAsync(profile, model: "gemini-2.5-flash");

        inner.Started.Should().BeTrue();
        inner.LastModel.Should().Be("gemini-2.5-flash");
        adapter.Profile.Should().Be(profile);
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
        PluginSessionEvent pluginEvent, Func<ClaudeSessionEvent, bool> isExpectedMapping)
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities);

        inner.Emit(pluginEvent);
        inner.Complete();

        var mapped = new List<ClaudeSessionEvent>();
        await foreach (var evt in adapter.Events)
        {
            mapped.Add(evt);
        }

        mapped.Should().ContainSingle().Which.Should().Match(evt => isExpectedMapping((ClaudeSessionEvent)evt));
    }

    public static IEnumerable<object[]> _EventMappings()
    {
        yield return
        [
            new PluginSessionInitialized { SessionId = "s1", Tools = ["read_file"] },
            (Func<ClaudeSessionEvent, bool>)(evt => evt is SessionInitialized init && init.SessionId == "s1" && init.Tools.Single() == "read_file"),
        ];
        yield return
        [
            new PluginAssistantTextDelta { SessionId = "s1", BlockIndex = 2, Text = "hi" },
            (Func<ClaudeSessionEvent, bool>)(evt => evt is AssistantTextDelta delta && delta.BlockIndex == 2 && delta.Text == "hi"),
        ];
        yield return
        [
            new PluginToolUseRequested { SessionId = "s1", ToolUseId = "t1", ToolName = "read_file", InputJson = "{}" },
            (Func<ClaudeSessionEvent, bool>)(evt => evt is ToolUseRequested tool && tool.ToolUseId == "t1" && tool.ToolName == "read_file"),
        ];
        yield return
        [
            new PluginToolResult { SessionId = "s1", ToolUseId = "t1", Content = "ok", IsError = false },
            (Func<ClaudeSessionEvent, bool>)(evt => evt is ToolResult result && result.Content == "ok" && !result.IsError),
        ];
        yield return
        [
            new PluginPermissionRequested { SessionId = "s1", ToolUseId = "t1", ToolName = "read_file", InputJson = "{}" },
            (Func<ClaudeSessionEvent, bool>)(evt => evt is PermissionRequested permission && permission.ToolUseId == "t1"),
        ];
        yield return
        [
            new PluginTurnCompleted { SessionId = "s1", Subtype = "success", Result = "done", IsError = false, StopReason = null },
            (Func<ClaudeSessionEvent, bool>)(evt => evt is TurnCompleted turn && turn.Subtype == "success" && turn.Result == "done" && !turn.IsError),
        ];
        yield return
        [
            new PluginSessionError { SessionId = "s1", Message = "boom" },
            (Func<ClaudeSessionEvent, bool>)(evt => evt is SessionError error && error.Message == "boom"),
        ];
    }
}
