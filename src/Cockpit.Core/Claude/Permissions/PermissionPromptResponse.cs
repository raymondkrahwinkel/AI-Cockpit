using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cockpit.Core.Claude.Permissions;

/// <summary>
/// Serializes a <see cref="PermissionDecision"/> into the JSON body the CLI's
/// <c>--permission-prompt-tool</c> expects as the tool-result text:
/// <c>{"behavior":"allow","updatedInput":{...}}</c> or
/// <c>{"behavior":"deny","message":"..."}</c> (verified against claude.exe 2.1.197).
/// </summary>
public static class PermissionPromptResponse
{
    /// <summary>
    /// Builds the behavior JSON for <paramref name="decision"/>. For an allow with no rewritten
    /// input, <paramref name="proposedInputJson"/> is echoed back as <c>updatedInput</c> (the CLI
    /// runs the tool with whatever <c>updatedInput</c> carries, so it must be the original input).
    /// </summary>
    public static string Serialize(PermissionDecision decision, string proposedInputJson)
    {
        if (!decision.IsAllowed)
        {
            var deny = new JsonObject
            {
                ["behavior"] = "deny",
                ["message"] = decision.DenyMessage ?? "Denied by the cockpit operator.",
            };
            return deny.ToJsonString();
        }

        var inputJson = decision.UpdatedInputJson ?? proposedInputJson;
        var allow = new JsonObject
        {
            ["behavior"] = "allow",
            ["updatedInput"] = ParseInputOrEmptyObject(inputJson),
        };
        return allow.ToJsonString();
    }

    private static JsonNode ParseInputOrEmptyObject(string? inputJson)
    {
        if (string.IsNullOrWhiteSpace(inputJson))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(inputJson) ?? new JsonObject();
        }
        catch (JsonException)
        {
            // A non-JSON input can never be a valid updatedInput object; fall back to empty so
            // the CLI still gets a well-formed allow response rather than a serialization crash.
            return new JsonObject();
        }
    }
}
