using System.Text.Json;

namespace Cockpit.Plugin.Docker.Mcp;

// Uniform `{ ok, ... }` JSON for the MCP tool return values, so every tool answers the same shape and an error
// never throws across the boundary.
internal static class McpText
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static string Error(string message) =>
        JsonSerializer.Serialize(new { ok = false, error = message }, Options);

    // `note` (AC-1348) is the gate's bypass line — "executed without asking — daemon mode: …" — merged into the
    // payload when a consent mode skipped the card, so the skip is visible in the transcript too.
    public static string Ok(object payload, string? note = null)
    {
        if (note is null)
        {
            return JsonSerializer.Serialize(payload, Options);
        }

        var node = JsonSerializer.SerializeToNode(payload, Options)!.AsObject();
        node["note"] = note;
        return node.ToJsonString(Options);
    }
}
