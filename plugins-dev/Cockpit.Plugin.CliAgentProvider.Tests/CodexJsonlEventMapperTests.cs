using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.CliAgentProvider.Tests;

// `CodexJsonlEventMapper` (#45 fase B1) against representative `codex exec --json` JSONL
// lines, per the design doc's event table (Cockpit-ProviderPlugins-PhaseB-CLI-2026-07-11.md §2.3) — the only
// CLI-specific logic in this plugin, so it is exercised as a pure function against fixtures rather than
// through a spawned process (no logged-in `codex` CLI in this environment; B2 to re-verify against a
// real transcript).
public class CodexJsonlEventMapperTests
{
    [Fact]
    public void ParseLine_ThreadStarted_EmitsSessionInitialized_AndCapturesTheThreadIdAsTheSessionId()
    {
        var result = CodexJsonlEventMapper.ParseLine("""{"type":"thread.started","thread_id":"thread-123"}""", sessionId: null);

        Assert.Equal("thread-123", result.SessionId);
        Assert.Equal("thread-123", Assert.IsType<PluginSessionInitialized>(Assert.Single(result.Events)).SessionId);
    }

    [Fact]
    public void ParseLine_ItemCompletedAgentMessage_EmitsOneAssistantTextDeltaWithTheFullText()
    {
        var result = CodexJsonlEventMapper.ParseLine(
            """{"type":"item.completed","item":{"id":"item_0","item_type":"agent_message","text":"Hello, world!"}}""",
            sessionId: "thread-123");

        Assert.Equal("thread-123", result.SessionId);
        var delta = Assert.IsType<PluginAssistantTextDelta>(Assert.Single(result.Events));
        Assert.Equal("Hello, world!", delta.Text);
        Assert.Equal(0, delta.BlockIndex);
    }

    [Fact]
    public void ParseLine_ItemStartedCommandExecution_EmitsToolUseRequested()
    {
        var result = CodexJsonlEventMapper.ParseLine(
            """{"type":"item.started","item":{"id":"item_1","item_type":"command_execution","command":"ls -la","status":"in_progress"}}""",
            sessionId: "thread-123");

        var toolUse = Assert.IsType<PluginToolUseRequested>(Assert.Single(result.Events));
        Assert.Equal("item_1", toolUse.ToolUseId);
        Assert.Equal("command_execution", toolUse.ToolName);
        Assert.Equal("\"ls -la\"", toolUse.InputJson);
    }

    [Fact]
    public void ParseLine_ItemCompletedCommandExecution_WithZeroExitCode_EmitsSuccessfulToolResult()
    {
        var result = CodexJsonlEventMapper.ParseLine(
            """{"type":"item.completed","item":{"id":"item_1","item_type":"command_execution","command":"ls -la","aggregated_output":"file1\nfile2","exit_code":0,"status":"completed"}}""",
            sessionId: "thread-123");

        var toolResult = Assert.IsType<PluginToolResult>(Assert.Single(result.Events));
        Assert.Equal("item_1", toolResult.ToolUseId);
        Assert.Equal("file1\nfile2", toolResult.Content);
        Assert.False(toolResult.IsError);
    }

    [Fact]
    public void ParseLine_ItemCompletedCommandExecution_WithNonZeroExitCode_EmitsFailedToolResult()
    {
        var result = CodexJsonlEventMapper.ParseLine(
            """{"type":"item.completed","item":{"id":"item_1","item_type":"command_execution","aggregated_output":"not found","exit_code":1,"status":"completed"}}""",
            sessionId: "thread-123");

        Assert.True(Assert.IsType<PluginToolResult>(Assert.Single(result.Events)).IsError);
    }

    [Fact]
    public void ParseLine_ItemStartedMcpToolCall_EmitsToolUseRequestedWithTheToolName()
    {
        var result = CodexJsonlEventMapper.ParseLine(
            """{"type":"item.started","item":{"id":"item_2","item_type":"mcp_tool_call","tool":"read_file","arguments":{"path":"a.txt"}}}""",
            sessionId: "thread-123");

        var toolUse = Assert.IsType<PluginToolUseRequested>(Assert.Single(result.Events));
        Assert.Equal("read_file", toolUse.ToolName);
        Assert.Equal("""{"path":"a.txt"}""", toolUse.InputJson);
    }

    [Fact]
    public void ParseLine_ItemCompletedReasoning_EmitsNoEvent()
    {
        var result = CodexJsonlEventMapper.ParseLine(
            """{"type":"item.completed","item":{"id":"item_3","item_type":"reasoning","text":"thinking..."}}""",
            sessionId: "thread-123");

        Assert.Empty(result.Events);
        Assert.Equal("thread-123", result.SessionId);
    }

    [Fact]
    public void ParseLine_TurnStarted_EmitsNoEvent()
    {
        var result = CodexJsonlEventMapper.ParseLine("""{"type":"turn.started"}""", sessionId: "thread-123");

        Assert.Empty(result.Events);
    }

    [Fact]
    public void ParseLine_TurnCompleted_EmitsASuccessfulTurnCompletedEvent()
    {
        var result = CodexJsonlEventMapper.ParseLine(
            """{"type":"turn.completed","usage":{"input_tokens":24763,"cached_input_tokens":24448,"output_tokens":122}}""",
            sessionId: "thread-123");

        var turnCompleted = Assert.IsType<PluginTurnCompleted>(Assert.Single(result.Events));
        Assert.Equal("success", turnCompleted.Subtype);
        Assert.False(turnCompleted.IsError);
    }

    [Fact]
    public void ParseLine_TurnFailed_EmitsASessionErrorFollowedByAFailedTurnCompleted()
    {
        var result = CodexJsonlEventMapper.ParseLine(
            """{"type":"turn.failed","error":{"message":"sandbox denied write access"}}""",
            sessionId: "thread-123");

        Assert.Equal(2, System.Linq.Enumerable.Count(result.Events));
        Assert.Equal("sandbox denied write access", Assert.IsType<PluginSessionError>(result.Events[0]).Message);
        Assert.True(Assert.IsType<PluginTurnCompleted>(result.Events[1]).IsError);
    }

    [Fact]
    public void ParseLine_TopLevelError_EmitsASessionErrorFollowedByAFailedTurnCompleted()
    {
        var result = CodexJsonlEventMapper.ParseLine("""{"type":"error","message":"connection reset"}""", sessionId: "thread-123");

        Assert.Equal(2, System.Linq.Enumerable.Count(result.Events));
        Assert.Equal("connection reset", Assert.IsType<PluginSessionError>(result.Events[0]).Message);
        Assert.True(Assert.IsType<PluginTurnCompleted>(result.Events[1]).IsError);
    }

    [Fact]
    public void ParseLine_UnrecognizedType_IsIgnoredRatherThanThrown_ForwardCompat()
    {
        var act = () => CodexJsonlEventMapper.ParseLine("""{"type":"item.deleted","item":{"id":"item_9"}}""", sessionId: "thread-123");

        act();
        Assert.Empty(act().Events);
    }

    [Fact]
    public void ParseLine_MalformedJson_IsSkippedRatherThanThrown()
    {
        var act = () => CodexJsonlEventMapper.ParseLine("{not valid json", sessionId: "thread-123");

        act();
        Assert.Empty(act().Events);
    }

    [Fact]
    public void ParseLine_BlankLine_IsSkipped()
    {
        var result = CodexJsonlEventMapper.ParseLine("   ", sessionId: "thread-123");

        Assert.Empty(result.Events);
        Assert.Equal("thread-123", result.SessionId);
    }
}
