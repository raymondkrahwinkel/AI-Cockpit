using System.Text.Json.Nodes;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.ClaudeProvider;

/// <summary>
/// Writes the shared MCP registry (#26) the host resolved for this session into a Claude <c>--mcp-config</c> file
/// and returns its path — the TTY mirror of what the host's <c>ClaudeTtySessionProvider._WriteRegistryMcpConfig</c>
/// did, now that the servers cross the plugin boundary on <see cref="PluginTtyLaunchContext.McpServers"/> (weg A).
/// No cockpit permission server here — the interactive TUI prompts for permission itself. Returns
/// <see langword="null"/> when there is nothing to add.
/// </summary>
internal static class ClaudeMcpConfig
{
    public static string? Write(IReadOnlyList<PluginMcpServer> servers)
    {
        if (servers.Count == 0)
        {
            return null;
        }

        var mcpServers = new JsonObject();
        foreach (var server in servers)
        {
            if (_ToEntry(server) is { } entry)
            {
                mcpServers[server.Name] = entry;
            }
        }

        if (mcpServers.Count == 0)
        {
            return null;
        }

        var root = new JsonObject { ["mcpServers"] = mcpServers };
        var directory = Path.Combine(Path.GetTempPath(), "cockpit-claude-mcp");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{Guid.NewGuid():N}.json");
        File.WriteAllText(path, root.ToJsonString());
        return path;
    }

    private static JsonObject? _ToEntry(PluginMcpServer server)
    {
        if (!string.IsNullOrWhiteSpace(server.Url))
        {
            var entry = new JsonObject { ["type"] = "http", ["url"] = server.Url };
            if (server.CockpitHosted)
            {
                // Reference the env var Claude Code expands at spawn (AC-40): ${COCKPIT_MCP_KEY}. The key never
                // lands in this file, so the config can stay a plain (world-readable) write.
                var envReference = "${" + WellKnownSessionEnvironment.CockpitMcpKey + "}";
                entry["headers"] = new JsonObject { ["Authorization"] = $"Bearer {envReference}" };
            }
            else if (!string.IsNullOrWhiteSpace(server.BearerToken))
            {
                entry["headers"] = new JsonObject { ["Authorization"] = $"Bearer {server.BearerToken}" };
            }

            return entry;
        }

        if (!string.IsNullOrWhiteSpace(server.Command))
        {
            var entry = new JsonObject { ["type"] = "stdio", ["command"] = server.Command };
            if (server.Args is { Count: > 0 })
            {
                entry["args"] = new JsonArray([.. server.Args.Select(argument => JsonValue.Create(argument))]);
            }

            return entry;
        }

        return null;
    }
}
