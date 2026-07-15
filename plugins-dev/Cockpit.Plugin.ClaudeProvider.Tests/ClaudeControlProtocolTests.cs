using System.Text.Json;
using FluentAssertions;

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

        ClaudeControlProtocol.TryParsePermissionRequest(document.RootElement, out var requestId, out var toolUseId, out var toolName, out var inputJson)
            .Should().BeTrue();

        requestId.Should().Be("req-1");
        toolUseId.Should().Be("toolu_9");
        toolName.Should().Be("Bash");
        JsonDocument.Parse(inputJson).RootElement.GetProperty("command").GetString().Should().Be("ls -la");
    }

    [Fact]
    public void TryParsePermissionRequest_FallsBackToRequestId_WhenNoToolUseId()
    {
        // tool_use_id is optional in the wire request (permission_request.get("tool_use_id")); the response still echoes
        // request_id, so the fallback only affects which transcript card the prompt attaches to.
        var line = """{"type":"control_request","request_id":"req-2","request":{"subtype":"can_use_tool","tool_name":"Read","input":{}}}""";
        using var document = JsonDocument.Parse(line);

        ClaudeControlProtocol.TryParsePermissionRequest(document.RootElement, out var requestId, out var toolUseId, out _, out _)
            .Should().BeTrue();

        requestId.Should().Be("req-2");
        toolUseId.Should().Be("req-2");
    }

    [Theory]
    [InlineData("""{"type":"control_response","response":{"subtype":"success","request_id":"req-1","response":{}}}""")]
    [InlineData("""{"type":"control_request","request_id":"x","request":{"subtype":"initialize"}}""")]
    [InlineData("""{"type":"control_cancel_request","request_id":"x"}""")]
    [InlineData("""{"type":"assistant","message":{"content":[]}}""")]
    public void TryParsePermissionRequest_IgnoresNonPermissionLines(string line)
    {
        using var document = JsonDocument.Parse(line);

        ClaudeControlProtocol.TryParsePermissionRequest(document.RootElement, out _, out _, out _, out _)
            .Should().BeFalse();
    }

    [Fact]
    public void BuildDecisionResponse_Allow_EchoesRequestIdAndOriginalInput()
    {
        var line = ClaudeControlProtocol.BuildDecisionResponse("req-1", allow: true, originalInputJson: """{"command":"ls"}""", denyMessage: "unused");

        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        root.GetProperty("type").GetString().Should().Be("control_response");

        var response = root.GetProperty("response");
        response.GetProperty("subtype").GetString().Should().Be("success");
        response.GetProperty("request_id").GetString().Should().Be("req-1");

        var decision = response.GetProperty("response");
        decision.GetProperty("behavior").GetString().Should().Be("allow");
        // updatedInput rides back as an object, not a re-escaped string.
        decision.GetProperty("updatedInput").GetProperty("command").GetString().Should().Be("ls");
    }

    [Fact]
    public void BuildDecisionResponse_Deny_CarriesBehaviorDenyAndMessage_StillSuccessSubtype()
    {
        var line = ClaudeControlProtocol.BuildDecisionResponse("req-9", allow: false, originalInputJson: "{}", denyMessage: "No.");

        using var document = JsonDocument.Parse(line);
        var response = document.RootElement.GetProperty("response");
        // A deny is a successful callback that returned a deny decision — subtype stays "success".
        response.GetProperty("subtype").GetString().Should().Be("success");

        var decision = response.GetProperty("response");
        decision.GetProperty("behavior").GetString().Should().Be("deny");
        decision.GetProperty("message").GetString().Should().Be("No.");
    }

    [Fact]
    public void BuildInitializeRequest_IsAControlRequestWithInitializeSubtype()
    {
        var line = ClaudeControlProtocol.BuildInitializeRequest("init-1");

        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        root.GetProperty("type").GetString().Should().Be("control_request");
        root.GetProperty("request_id").GetString().Should().Be("init-1");
        root.GetProperty("request").GetProperty("subtype").GetString().Should().Be("initialize");
    }
}
