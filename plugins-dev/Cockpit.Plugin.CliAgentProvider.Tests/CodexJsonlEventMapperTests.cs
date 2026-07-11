using Cockpit.Plugin.CliAgentProvider;
using Cockpit.Plugins.Abstractions.Sessions;
using FluentAssertions;

namespace Cockpit.Plugin.CliAgentProvider.Tests;

/// <summary>
/// <see cref="CodexJsonlEventMapper"/> (#45 fase B1) against representative <c>codex exec --json</c> JSONL
/// lines, per the design doc's event table (Cockpit-ProviderPlugins-PhaseB-CLI-2026-07-11.md §2.3) — the only
/// CLI-specific logic in this plugin, so it is exercised as a pure function against fixtures rather than
/// through a spawned process (no logged-in <c>codex</c> CLI in this environment; B2 to re-verify against a
/// real transcript).
/// </summary>
public class CodexJsonlEventMapperTests
{
    [Fact]
    public void ParseLine_ThreadStarted_EmitsSessionInitialized_AndCapturesTheThreadIdAsTheSessionId()
    {
        var result = CodexJsonlEventMapper.ParseLine("""{"type":"thread.started","thread_id":"thread-123"}""", sessionId: null);

        result.SessionId.Should().Be("thread-123");
        result.Events.Should().ContainSingle().Which.Should().BeOfType<PluginSessionInitialized>()
            .Which.SessionId.Should().Be("thread-123");
    }

    [Fact]
    public void ParseLine_ItemCompletedAgentMessage_EmitsOneAssistantTextDeltaWithTheFullText()
    {
        var result = CodexJsonlEventMapper.ParseLine(
            """{"type":"item.completed","item":{"id":"item_0","item_type":"agent_message","text":"Hello, world!"}}""",
            sessionId: "thread-123");

        result.SessionId.Should().Be("thread-123");
        var delta = result.Events.Should().ContainSingle().Which.Should().BeOfType<PluginAssistantTextDelta>().Subject;
        delta.Text.Should().Be("Hello, world!");
        delta.BlockIndex.Should().Be(0);
    }

    [Fact]
    public void ParseLine_ItemStartedCommandExecution_EmitsToolUseRequested()
    {
        var result = CodexJsonlEventMapper.ParseLine(
            """{"type":"item.started","item":{"id":"item_1","item_type":"command_execution","command":"ls -la","status":"in_progress"}}""",
            sessionId: "thread-123");

        var toolUse = result.Events.Should().ContainSingle().Which.Should().BeOfType<PluginToolUseRequested>().Subject;
        toolUse.ToolUseId.Should().Be("item_1");
        toolUse.ToolName.Should().Be("command_execution");
        toolUse.InputJson.Should().Be("\"ls -la\"");
    }

    [Fact]
    public void ParseLine_ItemCompletedCommandExecution_WithZeroExitCode_EmitsSuccessfulToolResult()
    {
        var result = CodexJsonlEventMapper.ParseLine(
            """{"type":"item.completed","item":{"id":"item_1","item_type":"command_execution","command":"ls -la","aggregated_output":"file1\nfile2","exit_code":0,"status":"completed"}}""",
            sessionId: "thread-123");

        var toolResult = result.Events.Should().ContainSingle().Which.Should().BeOfType<PluginToolResult>().Subject;
        toolResult.ToolUseId.Should().Be("item_1");
        toolResult.Content.Should().Be("file1\nfile2");
        toolResult.IsError.Should().BeFalse();
    }

    [Fact]
    public void ParseLine_ItemCompletedCommandExecution_WithNonZeroExitCode_EmitsFailedToolResult()
    {
        var result = CodexJsonlEventMapper.ParseLine(
            """{"type":"item.completed","item":{"id":"item_1","item_type":"command_execution","aggregated_output":"not found","exit_code":1,"status":"completed"}}""",
            sessionId: "thread-123");

        result.Events.Should().ContainSingle().Which.Should().BeOfType<PluginToolResult>()
            .Which.IsError.Should().BeTrue();
    }

    [Fact]
    public void ParseLine_ItemStartedMcpToolCall_EmitsToolUseRequestedWithTheToolName()
    {
        var result = CodexJsonlEventMapper.ParseLine(
            """{"type":"item.started","item":{"id":"item_2","item_type":"mcp_tool_call","tool":"read_file","arguments":{"path":"a.txt"}}}""",
            sessionId: "thread-123");

        var toolUse = result.Events.Should().ContainSingle().Which.Should().BeOfType<PluginToolUseRequested>().Subject;
        toolUse.ToolName.Should().Be("read_file");
        toolUse.InputJson.Should().Be("""{"path":"a.txt"}""");
    }

    [Fact]
    public void ParseLine_ItemCompletedReasoning_EmitsNoEvent()
    {
        var result = CodexJsonlEventMapper.ParseLine(
            """{"type":"item.completed","item":{"id":"item_3","item_type":"reasoning","text":"thinking..."}}""",
            sessionId: "thread-123");

        result.Events.Should().BeEmpty();
        result.SessionId.Should().Be("thread-123");
    }

    [Fact]
    public void ParseLine_TurnStarted_EmitsNoEvent()
    {
        var result = CodexJsonlEventMapper.ParseLine("""{"type":"turn.started"}""", sessionId: "thread-123");

        result.Events.Should().BeEmpty();
    }

    [Fact]
    public void ParseLine_TurnCompleted_EmitsASuccessfulTurnCompletedEvent()
    {
        var result = CodexJsonlEventMapper.ParseLine(
            """{"type":"turn.completed","usage":{"input_tokens":24763,"cached_input_tokens":24448,"output_tokens":122}}""",
            sessionId: "thread-123");

        var turnCompleted = result.Events.Should().ContainSingle().Which.Should().BeOfType<PluginTurnCompleted>().Subject;
        turnCompleted.Subtype.Should().Be("success");
        turnCompleted.IsError.Should().BeFalse();
    }

    [Fact]
    public void ParseLine_TurnFailed_EmitsASessionErrorFollowedByAFailedTurnCompleted()
    {
        var result = CodexJsonlEventMapper.ParseLine(
            """{"type":"turn.failed","error":{"message":"sandbox denied write access"}}""",
            sessionId: "thread-123");

        result.Events.Should().HaveCount(2);
        result.Events[0].Should().BeOfType<PluginSessionError>().Which.Message.Should().Be("sandbox denied write access");
        result.Events[1].Should().BeOfType<PluginTurnCompleted>().Which.IsError.Should().BeTrue();
    }

    [Fact]
    public void ParseLine_TopLevelError_EmitsASessionErrorFollowedByAFailedTurnCompleted()
    {
        var result = CodexJsonlEventMapper.ParseLine("""{"type":"error","message":"connection reset"}""", sessionId: "thread-123");

        result.Events.Should().HaveCount(2);
        result.Events[0].Should().BeOfType<PluginSessionError>().Which.Message.Should().Be("connection reset");
        result.Events[1].Should().BeOfType<PluginTurnCompleted>().Which.IsError.Should().BeTrue();
    }

    [Fact]
    public void ParseLine_UnrecognizedType_IsIgnoredRatherThanThrown_ForwardCompat()
    {
        var act = () => CodexJsonlEventMapper.ParseLine("""{"type":"item.deleted","item":{"id":"item_9"}}""", sessionId: "thread-123");

        act.Should().NotThrow();
        act().Events.Should().BeEmpty();
    }

    [Fact]
    public void ParseLine_MalformedJson_IsSkippedRatherThanThrown()
    {
        var act = () => CodexJsonlEventMapper.ParseLine("{not valid json", sessionId: "thread-123");

        act.Should().NotThrow();
        act().Events.Should().BeEmpty();
    }

    [Fact]
    public void ParseLine_BlankLine_IsSkipped()
    {
        var result = CodexJsonlEventMapper.ParseLine("   ", sessionId: "thread-123");

        result.Events.Should().BeEmpty();
        result.SessionId.Should().Be("thread-123");
    }
}
