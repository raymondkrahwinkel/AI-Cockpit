using System.Text.Json;

namespace Cockpit.Plugin.ClaudeProvider.Tests;

// `ClaudeControlProtocol` (Fase 4) — the in-band stdio permission channel's wire format, anchored to the
// exact shapes the official Agent SDK transport uses (`claude-agent-sdk-python query.py`): an inbound
// `can_use_tool` control_request, and the `control_response` allow/deny answer echoing its `request_id`.
// These tests are the single place the wire assumptions live, so a live field-name drift is a one-line fix.
public class ClaudeControlProtocolTests
{
    [Fact]
    public void TryParsePermissionRequest_ExtractsRequestIdToolUseIdNameAndInput()
    {
        var line = """
        {"type":"control_request","request_id":"req-1","request":{"subtype":"can_use_tool","tool_name":"Bash","input":{"command":"ls -la"},"tool_use_id":"toolu_9"}}
        """;
        using var document = JsonDocument.Parse(line);

        Assert.True(ClaudeControlProtocol.TryParsePermissionRequest(document.RootElement, out var requestId, out var toolUseId, out var toolName, out var inputJson));

        Assert.Equal("req-1", requestId);
        Assert.Equal("toolu_9", toolUseId);
        Assert.Equal("Bash", toolName);
        Assert.Equal("ls -la", JsonDocument.Parse(inputJson).RootElement.GetProperty("command").GetString());
    }

    [Fact]
    public void TryParsePermissionRequest_FallsBackToRequestId_WhenNoToolUseId()
    {
        // tool_use_id is optional in the wire request (permission_request.get("tool_use_id")); the response still echoes
        // request_id, so the fallback only affects which transcript card the prompt attaches to.
        var line = """{"type":"control_request","request_id":"req-2","request":{"subtype":"can_use_tool","tool_name":"Read","input":{}}}""";
        using var document = JsonDocument.Parse(line);

        Assert.True(ClaudeControlProtocol.TryParsePermissionRequest(document.RootElement, out var requestId, out var toolUseId, out _, out _));

        Assert.Equal("req-2", requestId);
        Assert.Equal("req-2", toolUseId);
    }

    [Theory]
    [InlineData("""{"type":"control_response","response":{"subtype":"success","request_id":"req-1","response":{}}}""")]
    [InlineData("""{"type":"control_request","request_id":"x","request":{"subtype":"initialize"}}""")]
    [InlineData("""{"type":"control_cancel_request","request_id":"x"}""")]
    [InlineData("""{"type":"assistant","message":{"content":[]}}""")]
    public void TryParsePermissionRequest_IgnoresNonPermissionLines(string line)
    {
        using var document = JsonDocument.Parse(line);

        Assert.False(ClaudeControlProtocol.TryParsePermissionRequest(document.RootElement, out _, out _, out _, out _));
    }

    [Fact]
    public void BuildDecisionResponse_Allow_EchoesRequestIdAndOriginalInput()
    {
        var line = ClaudeControlProtocol.BuildDecisionResponse("req-1", allow: true, originalInputJson: """{"command":"ls"}""", denyMessage: "unused");

        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        Assert.Equal("control_response", root.GetProperty("type").GetString());

        var response = root.GetProperty("response");
        Assert.Equal("success", response.GetProperty("subtype").GetString());
        Assert.Equal("req-1", response.GetProperty("request_id").GetString());

        var decision = response.GetProperty("response");
        Assert.Equal("allow", decision.GetProperty("behavior").GetString());
        // updatedInput rides back as an object, not a re-escaped string.
        Assert.Equal("ls", decision.GetProperty("updatedInput").GetProperty("command").GetString());
    }

    [Fact]
    public void BuildDecisionResponse_Allow_WithAnswers_MergesThemIntoUpdatedInputBesideTheQuestions()
    {
        // AC-715: the AskUserQuestion contract — the SDK reads `updatedInput.answers`, keyed by question text,
        // alongside the `questions` it sent. An allow that echoes only the input approves the question and never
        // answers it, which is what the cockpit did before this.
        const string questions = """{"questions":[{"question":"Which tests?","header":"Tests","options":[{"label":"Both"}],"multiSelect":false}]}""";

        var line = ClaudeControlProtocol.BuildDecisionResponse(
            "req-4", allow: true, originalInputJson: questions, denyMessage: "unused", answersJson: """{"Which tests?":"Both"}""");

        using var document = JsonDocument.Parse(line);
        var updatedInput = document.RootElement.GetProperty("response").GetProperty("response").GetProperty("updatedInput");

        Assert.Equal("Both", updatedInput.GetProperty("answers").GetProperty("Which tests?").GetString());
        Assert.Equal("Which tests?", updatedInput.GetProperty("questions")[0].GetProperty("question").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("\"a string, not an object\"")]
    public void BuildDecisionResponse_Allow_WithoutUsableAnswers_LeavesTheInputAlone(string? answersJson)
    {
        // A tool that was never a question, and a garbled answers document, take the same path: echo the input as
        // it came, rather than failing the whole response or inventing an empty `answers` the agent would read.
        var line = ClaudeControlProtocol.BuildDecisionResponse(
            "req-5", allow: true, originalInputJson: """{"command":"ls"}""", denyMessage: "unused", answersJson);

        using var document = JsonDocument.Parse(line);
        var updatedInput = document.RootElement.GetProperty("response").GetProperty("response").GetProperty("updatedInput");

        Assert.Equal("ls", updatedInput.GetProperty("command").GetString());
        Assert.False(updatedInput.TryGetProperty("answers", out _));
    }

    [Fact]
    public void BuildDecisionResponse_Deny_CarriesBehaviorDenyAndMessage_StillSuccessSubtype()
    {
        var line = ClaudeControlProtocol.BuildDecisionResponse("req-9", allow: false, originalInputJson: "{}", denyMessage: "No.");

        using var document = JsonDocument.Parse(line);
        var response = document.RootElement.GetProperty("response");
        // A deny is a successful callback that returned a deny decision — subtype stays "success".
        Assert.Equal("success", response.GetProperty("subtype").GetString());

        var decision = response.GetProperty("response");
        Assert.Equal("deny", decision.GetProperty("behavior").GetString());
        Assert.Equal("No.", decision.GetProperty("message").GetString());
    }

    [Fact]
    public void BuildInitializeRequest_IsAControlRequestWithInitializeSubtype()
    {
        var line = ClaudeControlProtocol.BuildInitializeRequest("init-1");

        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        Assert.Equal("control_request", root.GetProperty("type").GetString());
        Assert.Equal("init-1", root.GetProperty("request_id").GetString());
        Assert.Equal("initialize", root.GetProperty("request").GetProperty("subtype").GetString());
    }
}
