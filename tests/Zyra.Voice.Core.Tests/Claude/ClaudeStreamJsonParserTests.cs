using FluentAssertions;
using Zyra.Voice.Core.Claude;
using Zyra.Voice.Infrastructure.Claude;

namespace Zyra.Voice.Core.Tests.Claude;

/// <summary>
/// Fixtures modeled on the documented stream-json shapes from
/// https://code.claude.com/docs/en/headless.md ("Stream responses", system/init field table)
/// and https://code.claude.com/docs/en/agent-sdk/streaming-vs-single-mode.md (user message
/// envelope). No logged-in <c>claude</c> CLI was available to capture a real transcript in
/// this sandbox, so tool_use/tool_result content-block fixtures follow the well-known
/// Anthropic Messages API content-block schema that Claude Code's assistant/user events reuse.
/// </summary>
public class ClaudeStreamJsonParserTests
{
    [Fact]
    public void TryParseLine_SystemInit_ReturnsSessionInitialized()
    {
        const string line = """
            {"type":"system","subtype":"init","session_id":"sess-123","model":"claude-opus-4-6","tools":["Read","Edit","Bash"]}
            """;

        var result = ClaudeStreamJsonParser.TryParseLine(line);

        result.Should().BeOfType<SessionInitialized>().Which.Should().BeEquivalentTo(new SessionInitialized
        {
            SessionId = "sess-123",
            Model = "claude-opus-4-6",
            Tools = ["Read", "Edit", "Bash"],
        });
    }

    [Fact]
    public void TryParseLine_StreamEventTextDelta_ReturnsAssistantTextDelta()
    {
        const string line = """
            {"type":"stream_event","session_id":"sess-123","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hello"}}}
            """;

        var result = ClaudeStreamJsonParser.TryParseLine(line);

        result.Should().BeOfType<AssistantTextDelta>().Which.Text.Should().Be("Hello");
    }

    [Fact]
    public void TryParseLine_StreamEventNonTextDelta_ReturnsNull()
    {
        const string line = """
            {"type":"stream_event","session_id":"sess-123","event":{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"foo\":"}}}
            """;

        var result = ClaudeStreamJsonParser.TryParseLine(line);

        result.Should().BeNull();
    }

    [Fact]
    public void TryParseLine_AssistantTextBlock_ReturnsAssistantTextCompleted()
    {
        const string line = """
            {"type":"assistant","session_id":"sess-123","message":{"role":"assistant","content":[{"type":"text","text":"Sure, I can help."}]}}
            """;

        var result = ClaudeStreamJsonParser.TryParseLine(line);

        result.Should().BeOfType<AssistantTextCompleted>().Which.Text.Should().Be("Sure, I can help.");
    }

    [Fact]
    public void TryParseLine_AssistantToolUseBlock_ReturnsToolUseRequested()
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
    public void ParseAssistantContentBlocks_MultipleBlocks_ReturnsOneEventPerBlock()
    {
        const string line = """
            {"type":"assistant","session_id":"sess-123","message":{"role":"assistant","content":[{"type":"text","text":"Let me check."},{"type":"tool_use","id":"toolu_02","name":"Bash","input":{"command":"ls"}}]}}
            """;

        using var document = System.Text.Json.JsonDocument.Parse(line);
        var events = ClaudeStreamJsonParser.ParseAssistantContentBlocks(document.RootElement, "sess-123").ToList();

        events.Should().HaveCount(2);
        events[0].Should().BeOfType<AssistantTextCompleted>();
        events[1].Should().BeOfType<ToolUseRequested>();
    }

    [Fact]
    public void TryParseLine_UserToolResultTextContent_ReturnsToolResult()
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
    public void TryParseLine_UserToolResultArrayContent_ConcatenatesTextBlocks()
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
    public void TryParseLine_ResultSuccess_ReturnsTurnCompleted()
    {
        const string line = """
            {"type":"result","subtype":"success","is_error":false,"result":"Done.","session_id":"sess-123"}
            """;

        var result = ClaudeStreamJsonParser.TryParseLine(line);

        result.Should().BeOfType<TurnCompleted>().Which.Should().BeEquivalentTo(new TurnCompleted
        {
            SessionId = "sess-123",
            Subtype = "success",
            Result = "Done.",
            IsError = false,
        });
    }

    [Fact]
    public void TryParseLine_ResultError_ReturnsTurnCompletedWithIsErrorTrue()
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
    public void TryParseLine_UnknownType_ReturnsNull()
    {
        const string line = """{"type":"some_future_event","session_id":"sess-123"}""";

        var result = ClaudeStreamJsonParser.TryParseLine(line);

        result.Should().BeNull();
    }

    [Fact]
    public void TryParseLine_BlankLine_ReturnsNull()
    {
        ClaudeStreamJsonParser.TryParseLine("").Should().BeNull();
        ClaudeStreamJsonParser.TryParseLine("   ").Should().BeNull();
    }

    [Fact]
    public void TryParseLine_SystemNonInitSubtype_ReturnsNull()
    {
        const string line = """
            {"type":"system","subtype":"api_retry","attempt":1,"max_retries":3,"retry_delay_ms":500,"error_status":529,"error":"overloaded","uuid":"u1","session_id":"sess-123"}
            """;

        var result = ClaudeStreamJsonParser.TryParseLine(line);

        result.Should().BeNull();
    }
}
