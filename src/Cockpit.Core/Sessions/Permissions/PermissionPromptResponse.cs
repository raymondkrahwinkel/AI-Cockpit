using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cockpit.Core.Sessions.Permissions;

// Serializes a `PermissionDecision` into the JSON body the CLI's
// `--permission-prompt-tool` expects as the tool-result text:
// `{"behavior":"allow","updatedInput":{...}}` or
// `{"behavior":"deny","message":"..."}` (verified against claude.exe 2.1.197).
public static class PermissionPromptResponse
{
    // Builds the behavior JSON for `decision`. For an allow with no rewritten
    // input, `proposedInputJson` is echoed back as `updatedInput` (the CLI
    // runs the tool with whatever `updatedInput` carries, so it must be the original input).
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
