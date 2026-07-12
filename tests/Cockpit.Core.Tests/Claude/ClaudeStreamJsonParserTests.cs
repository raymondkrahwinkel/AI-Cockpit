using FluentAssertions;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Sessions;

namespace Cockpit.Core.Tests.Claude;

/// <summary>
/// Fixtures are the exact lines captured from a live <c>claude.exe</c> v2.1.197 stream-json
/// transcript, per <c>Memory/Zyra-Voice/StreamJson-Schema.md</c> (session/message ids
/// shortened). <c>tool_use</c>/<c>tool_result</c> content-block shapes remain the well-known
/// Anthropic Messages API schema, documented but not yet verified against a captured live
/// tool-turn — see the parser's remarks.
/// </summary>
public class ClaudeStreamJsonParserTests
{
    [Fact]
    public void ParseLine_SystemInit_ReturnsSessionInitialized()
    {
        const string line = """
            {"type":"system","subtype":"init","cwd":"C:\\Users\\raymo","session_id":"S1","tools":["Task","Bash","Read"]}
            """;

        var result = ClaudeStreamJsonParser.TryParseLine(line);

        result.Should().BeOfType<SessionInitialized>().Which.Should().BeEquivalentTo(new SessionInitialized
        {
            SessionId = "S1",
            Cwd = @"C:\Users\raymo",
            Tools = ["Task", "Bash", "Read"],
        });
    }

    [Fact]
    public void ParseLine_StreamEventMessageStart_ReturnsNoEvent()
    {
        const string line = """
            {"type":"stream_event","event":{"type":"message_start","message":{"model":"claude-opus-4-8","id":"msg_1","type":"message","role":"assistant","content":[],"stop_reason":null,"usage":{"input_tokens":2949}}},"session_id":"S1","parent_tool_use_id":null,"uuid":"u1"}
            """;

        var result = ClaudeStreamJsonParser.ParseLine(line);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ParseLine_StreamEventThinkingBlockStart_ReturnsNoEvent()
    {
        const string line = """
            {"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}},"session_id":"S1","parent_tool_use_id":null,"uuid":"u2"}
            """;

        var result = ClaudeStreamJsonParser.ParseLine(line);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ParseLine_StreamEventTextDelta_ReturnsAssistantTextDeltaWithParentToolUseIdAndUuid()
    {
        const string line = """
            {"type":"stream_event","event":{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"hallo Raymond"}},"session_id":"S1","parent_tool_use_id":null,"uuid":"u3"}
            """;

        var result = ClaudeStreamJsonParser.TryParseLine(line);

        var delta = result.Should().BeOfType<AssistantTextDelta>().Subject;
        delta.Text.Should().Be("hallo Raymond");
        delta.BlockIndex.Should().Be(1);
        delta.SessionId.Should().Be("S1");
        delta.ParentToolUseId.Should().BeNull();
        delta.Uuid.Should().Be("u3");
    }

    [Fact]
    public void ParseLine_StreamEventThinkingDelta_ReturnsAssistantThinkingDelta_SeparateFromText()
    {
        const string line = """
            {"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"Let me think about this."}},"session_id":"S1","parent_tool_use_id":null,"uuid":"u2b"}
            """;

        var result = ClaudeStreamJsonParser.TryParseLine(line);

        result.Should().BeOfType<AssistantThinkingDelta>().Which.Thinking.Should().Be("Let me think about this.");
        result.Should().NotBeOfType<AssistantTextDelta>();
    }

    [Fact]
    public void ParseLine_StreamEventSignatureDelta_ReturnsNoEvent()
    {
        const string line = """
            {"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"abc"}},"session_id":"S1","parent_tool_use_id":null,"uuid":"u2c"}
            """;

        var result = ClaudeStreamJsonParser.ParseLine(line);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ParseLine_AssistantSnapshot_ReturnsAssistantTextCompleted()
    {
        const string line = """
            {"type":"assistant","message":{"model":"claude-opus-4-8","id":"msg_1","role":"assistant","content":[{"type":"text","text":"hallo Raymond"}],"stop_reason":null,"usage":{"input_tokens":2949}}}
            """;

        var result = ClaudeStreamJsonParser.TryParseLine(line);

        result.Should().BeOfType<AssistantTextCompleted>().Which.Text.Should().Be("hallo Raymond");
    }

    [Fact]
    public void ParseLine_StreamEventMessageDelta_ReturnsNoEvent()
    {
        const string line = """
            {"type":"stream_event","event":{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":128}},"session_id":"S1","parent_tool_use_id":null,"uuid":"u4"}
            """;

        var result = ClaudeStreamJsonParser.ParseLine(line);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ParseLine_PostTurnSummary_ReturnsSessionStatusChangedWithStatusFields()
    {
        const string line = """
            {"type":"system","subtype":"post_turn_summary","summarizes_uuid":"x","status_category":"review_ready","status_detail":"done","needs_action":"","uuid":"u5","session_id":"S1"}
            """;

        var result = ClaudeStreamJsonParser.TryParseLine(line);

        var status = result.Should().BeOfType<SessionStatusChanged>().Subject;
        status.StatusCategory.Should().Be("review_ready");
        status.StatusDetail.Should().Be("done");
        status.NeedsAction.Should().Be(string.Empty);
        status.NotificationText.Should().BeNull();
        status.NotificationPriority.Should().BeNull();
        status.SessionId.Should().Be("S1");
    }

    [Fact]
    public void ParseLine_Notification_ReturnsSessionStatusChangedWithNotificationFields()
    {
        const string line = """
            {"type":"system","subtype":"notification","key":"stop-hook-error","text":"Stop hook error occurred","priority":"immediate","uuid":"u6","session_id":"S1"}
            """;

        var result = ClaudeStreamJsonParser.TryParseLine(line);

        var status = result.Should().BeOfType<SessionStatusChanged>().Subject;
        status.NotificationText.Should().Be("Stop hook error occurred");
        status.NotificationPriority.Should().Be("immediate");
        status.StatusCategory.Should().BeNull();
    }

    [Fact]
    public void ParseLine_RateLimitEvent_ReturnsRateLimitInfo()
    {
        const string line = """
            {"type":"rate_limit_event","rate_limit_info":{"status":"allowed","resetsAt":1783260000,"rateLimitType":"five_hour","overageStatus":"rejected","isUsingOverage":false},"uuid":"u7","session_id":"S1"}
            """;

        var result = ClaudeStreamJsonParser.TryParseLine(line);

        var rateLimit = result.Should().BeOfType<RateLimitInfo>().Subject;
        rateLimit.Status.Should().Be("allowed");
        rateLimit.RateLimitType.Should().Be("five_hour");
        rateLimit.ResetsAt.Should().Be(1783260000);
    }

    [Fact]
    public void ParseLine_ResultSuccess_ReturnsTurnCompletedWithTerminalReason()
    {
        const string line = """
            {"type":"result","subtype":"success","is_error":false,"result":"hallo Raymond","stop_reason":"end_turn","session_id":"S1","num_turns":1,"terminal_reason":"completed"}
            """;

        var result = ClaudeStreamJsonParser.TryParseLine(line);

        var turn = result.Should().BeOfType<TurnCompleted>().Subject;
        turn.Subtype.Should().Be("success");
        turn.Result.Should().Be("hallo Raymond");
        turn.IsError.Should().BeFalse();
        turn.StopReason.Should().Be("end_turn");
        turn.TerminalReason.Should().Be("completed");
        turn.SessionId.Should().Be("S1");
    }

    [Fact]
    public void ParseLine_ResultError_ReturnsTurnCompletedWithIsErrorTrue()
    {
        const string line = """
            {"type":"result","subtype":"error_max_turns","is_error":true,"result":null,"session_id":"sess-123"}
            """;

        var result = ClaudeStreamJsonParser.TryParseLine(line);

        var turn = result.Should().BeOfType<TurnCompleted>().Subject;
        turn.Subtype.Should().Be("error_max_turns");
        turn.IsError.Should().BeTrue();
        turn.Result.Should().BeNull();
    }

    [Fact]
    public void ParseLine_ResultWithUsageAndCost_CapturesTokensCostAndTurns()
    {
        const string line = """
            {"type":"result","subtype":"success","is_error":false,"result":"done","session_id":"S1","num_turns":3,"total_cost_usd":0.0123,"usage":{"input_tokens":100,"output_tokens":200,"cache_read_input_tokens":50,"cache_creation_input_tokens":10}}
            """;

        var result = ClaudeStreamJsonParser.TryParseLine(line);

        var turn = result.Should().BeOfType<TurnCompleted>().Subject;
        turn.NumTurns.Should().Be(3);
        turn.TotalCostUsd.Should().Be(0.0123);
        turn.Usage.Should().Be(new TokenUsage(100, 200, 50, 10));
        turn.Usage!.Total.Should().Be(360);
    }

    [Fact]
    public void ParseLine_ResultWithoutUsage_LeavesUsageCostAndTurnsNull()
    {
        const string line = """
            {"type":"result","subtype":"success","is_error":false,"result":"done","session_id":"S1"}
            """;

        var result = ClaudeStreamJsonParser.TryParseLine(line);

        var turn = result.Should().BeOfType<TurnCompleted>().Subject;
        turn.Usage.Should().BeNull();
        turn.TotalCostUsd.Should().BeNull();
        turn.NumTurns.Should().BeNull();
    }

    [Fact]
    public void ParseLine_AssistantToolUseBlock_ReturnsToolUseRequested()
    {
        const string line = """
            {"type":"assistant","session_id":"sess-123","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_01","name":"Read","input":{"file_path":"C:/foo.txt"}}]}}
            """;

        var result = ClaudeStreamJsonParser.TryParseLine(line);

        var toolUse = result.Should().BeOfType<ToolUseRequested>().Subject;
        toolUse.ToolUseId.Should().Be("toolu_01");
        toolUse.ToolName.Should().Be("Read");
        toolUse.InputJson.Should().Contain("file_path");
    }

    [Fact]
    public void ParseLine_AssistantMultipleBlocks_ReturnsOneEventPerBlock()
    {
        const string line = """
            {"type":"assistant","session_id":"sess-123","message":{"role":"assistant","content":[{"type":"text","text":"Let me check."},{"type":"tool_use","id":"toolu_02","name":"Bash","input":{"command":"ls"}}]}}
            """;

        var events = ClaudeStreamJsonParser.ParseLine(line).ToList();

        events.Should().HaveCount(2);
        events[0].Should().BeOfType<AssistantTextCompleted>();
        events[1].Should().BeOfType<ToolUseRequested>();
    }

    [Fact]
    public void ParseLine_UserToolResultTextContent_ReturnsToolResult()
    {
        const string line = """
            {"type":"user","session_id":"sess-123","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_01","content":"file contents here","is_error":false}]}}
            """;

        var result = ClaudeStreamJsonParser.TryParseLine(line);

        var toolResult = result.Should().BeOfType<ToolResult>().Subject;
        toolResult.ToolUseId.Should().Be("toolu_01");
        toolResult.Content.Should().Be("file contents here");
        toolResult.IsError.Should().BeFalse();
    }

    [Fact]
    public void ParseLine_UserToolResultArrayContent_ConcatenatesTextBlocks()
    {
        const string line = """
            {"type":"user","session_id":"sess-123","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_03","content":[{"type":"text","text":"line1\n"},{"type":"text","text":"line2"}],"is_error":true}]}}
            """;

        var result = ClaudeStreamJsonParser.TryParseLine(line);

        var toolResult = result.Should().BeOfType<ToolResult>().Subject;
        toolResult.Content.Should().Be("line1\nline2");
        toolResult.IsError.Should().BeTrue();
    }

    [Fact]
    public void ParseLine_UnknownTopLevelType_ReturnsUnknownEvent_DoesNotThrow()
    {
        const string line = """{"type":"some_future_event","session_id":"sess-123"}""";

        var result = ClaudeStreamJsonParser.TryParseLine(line);

        result.Should().BeOfType<UnknownEvent>().Which.SessionId.Should().Be("sess-123");
    }

    [Fact]
    public void ParseLine_SystemNonModeledSubtype_ReturnsUnknownEvent_DoesNotThrow()
    {
        const string line = """
            {"type":"system","subtype":"status","status":"requesting","session_id":"sess-123"}
            """;

        var result = ClaudeStreamJsonParser.TryParseLine(line);

        result.Should().BeOfType<UnknownEvent>();
    }

    [Fact]
    public void ParseLine_SystemThinkingTokensSubtype_ReturnsUnknownEvent_DoesNotThrow()
    {
        const string line = """
            {"type":"system","subtype":"thinking_tokens","estimated_tokens":128,"session_id":"sess-123"}
            """;

        var result = ClaudeStreamJsonParser.TryParseLine(line);

        result.Should().BeOfType<UnknownEvent>();
    }

    [Fact]
    public void ParseLine_StreamEventUnknownEventType_ReturnsUnknownEvent_DoesNotThrow()
    {
        const string line = """
            {"type":"stream_event","event":{"type":"some_future_stream_event"},"session_id":"sess-123","uuid":"u9"}
            """;

        var result = ClaudeStreamJsonParser.TryParseLine(line);

        result.Should().BeOfType<UnknownEvent>();
    }

    [Fact]
    public void ParseLine_MissingTypeField_ReturnsUnknownEvent_DoesNotThrow()
    {
        const string line = """{"session_id":"sess-123","foo":"bar"}""";

        var result = ClaudeStreamJsonParser.TryParseLine(line);

        result.Should().BeOfType<UnknownEvent>();
    }

    [Fact]
    public void ParseLine_BlankLine_ReturnsNoEvent()
    {
        ClaudeStreamJsonParser.ParseLine("").Should().BeEmpty();
        ClaudeStreamJsonParser.ParseLine("   ").Should().BeEmpty();
    }
}
