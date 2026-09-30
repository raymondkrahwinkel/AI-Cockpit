using System.Text.Json.Nodes;
using Cockpit.Core.Sessions.Permissions;

namespace Cockpit.Core.Tests.Claude;

/// <summary>
/// Locks the <c>--permission-prompt-tool</c> response contract verified against claude.exe
/// 2.1.197: allow carries <c>behavior</c>+<c>updatedInput</c>, deny carries
/// <c>behavior</c>+<c>message</c>.
/// </summary>
public class PermissionPromptResponseTests
{
    // Allow echoes the proposed input as updatedInput (or uses a rewrite of it, or an empty object when the proposal is
    // not JSON); deny carries behavior and message and no updatedInput. Compared whole, so a stray extra field fails too.
    [Theory]
    [InlineData("allow", null, """{"file_path":"a.txt","content":"hi"}""", """{"behavior":"allow","updatedInput":{"file_path":"a.txt","content":"hi"}}""")]
    [InlineData("allow", """{"file_path":"safe.txt"}""", """{"file_path":"a.txt"}""", """{"behavior":"allow","updatedInput":{"file_path":"safe.txt"}}""")]
    [InlineData("deny", "nope", "{}", """{"behavior":"deny","message":"nope"}""")]
    [InlineData("allow", null, "not json", """{"behavior":"allow","updatedInput":{}}""")]
    public void Serialize_WritesTheResponseClaudeExpects(string outcome, string? rewrittenOrMessage, string proposedInputJson, string expectedJson)
    {
        var decision = outcome == "deny" ? PermissionDecision.Deny(rewrittenOrMessage ?? string.Empty) : PermissionDecision.Allow(rewrittenOrMessage);

        var json = PermissionPromptResponse.Serialize(decision, proposedInputJson);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expectedJson), JsonNode.Parse(json)), json);
    }
}
