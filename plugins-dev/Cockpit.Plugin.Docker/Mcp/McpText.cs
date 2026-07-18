using System.Text.Json;

namespace Cockpit.Plugin.Docker.Mcp;

/// <summary>
/// Uniform <c>{ ok, ... }</c> JSON for the MCP tool return values, so every tool answers the same shape and an error
/// never throws across the boundary.
/// </summary>
internal static class McpText
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static string Error(string message) =>
        JsonSerializer.Serialize(new { ok = false, error = message }, Options);

    public static string Ok(object payload) => JsonSerializer.Serialize(payload, Options);
}
