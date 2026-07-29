using System.Text.Json;

namespace Cockpit.Plugin.ClaudeProvider.Tests;

/// <summary>
/// <see cref="ClaudeControlProtocol"/> (Fase 4) — the in-band stdio permission channel's wire format, anchored to the
/// exact shapes the official Agent SDK transport uses (<c>claude-agent-sdk-python query.py</c>): an inbound
/// <c>can_use_tool</c> control_request, and the <c>control_response</c> allow/deny answer echoing its <c>request_id</c>.
/// These tests are the single place the wire assumptions live, so a live field-name drift is a one-line fix.
/// </summary>
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
