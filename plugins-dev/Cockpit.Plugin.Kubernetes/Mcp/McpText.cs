using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cockpit.Plugin.Kubernetes.Mcp;

// JSON result helpers for the MCP tools — a uniform `{ ok, ... }` shape so an agent can tell success from a handled failure without exceptions crossing the boundary.
internal static class McpText
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static string Error(string message) => JsonSerializer.Serialize(new { ok = false, error = message }, Options);

    // `note` (AC-1349) is the gate's bypass line — "Executed without asking — cluster mode: …" — merged into the
    // payload when a consent mode skipped the card, so the skip is visible in the transcript too.
    public static string Ok(object payload, string? note = null) =>
        note is null ? JsonSerializer.Serialize(payload, Options) : Node(JsonSerializer.SerializeToNode(payload, Options), note);

    public static string Node(JsonNode? node, string? note = null)
    {
        // A payload with a note of its own (port_forward's) keeps it, after the bypass line.
        if (note is not null && node is JsonObject payload)
        {
            payload["note"] = payload["note"] is JsonValue existing ? $"{note} {existing}" : note;
        }

        return node?.ToJsonString(Options) ?? "null";
    }
}
