using System.Text.Json.Nodes;

namespace Cockpit.Core.Claude.Permissions;

/// <summary>
/// Builds the <c>--mcp-config</c> body that points every session at the cockpit's shared
/// in-process HTTP MCP server, e.g.
/// <c>{"mcpServers":{"cockpit":{"type":"http","url":"http://127.0.0.1:&lt;port&gt;/mcp"}}}</c>.
/// Pure string/JSON generation so it is unit-testable; writing it to disk is the host's job.
/// </summary>
public static class McpConfigFile
{
    /// <summary>The server key; the tool is addressed as <c>mcp__cockpit__permission_prompt</c>.</summary>
    public const string ServerName = "cockpit";

    /// <summary>Serializes the config JSON for a server reachable at <paramref name="mcpUrl"/>.</summary>
    public static string Serialize(string mcpUrl)
    {
        if (string.IsNullOrWhiteSpace(mcpUrl))
        {
            throw new ArgumentException("MCP url must be provided.", nameof(mcpUrl));
        }

        var config = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                [ServerName] = new JsonObject
                {
                    ["type"] = "http",
                    ["url"] = mcpUrl,
                },
            },
        };

        return config.ToJsonString();
    }
}
