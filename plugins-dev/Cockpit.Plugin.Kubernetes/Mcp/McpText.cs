using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cockpit.Plugin.Kubernetes.Mcp;

// JSON result helpers for the MCP tools — a uniform `{ ok, ... }` shape so an agent can tell success from a handled failure without exceptions crossing the boundary.
internal static class McpText
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static string Error(string message) => JsonSerializer.Serialize(new { ok = false, error = message }, Options);

    public static string Ok(object payload) => JsonSerializer.Serialize(payload, Options);

    public static string Node(JsonNode? node) => node?.ToJsonString(Options) ?? "null";
}
