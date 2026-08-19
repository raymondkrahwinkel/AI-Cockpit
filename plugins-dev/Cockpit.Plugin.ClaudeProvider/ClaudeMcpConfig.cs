using System.Text.Json.Nodes;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.ClaudeProvider;

// Writes the shared MCP registry (#26) the host resolved for this session into a Claude `--mcp-config` file
// and returns its path — the TTY mirror of what the host's `ClaudeTtySessionProvider._WriteRegistryMcpConfig`
// did, now that the servers cross the plugin boundary on `PluginTtyLaunchContext.McpServers` (weg A).
// No cockpit permission server here — the interactive TUI prompts for permission itself. Returns
// `null` when there is nothing to add, unless `writeEmptyExplicit` says otherwise.
internal static class ClaudeMcpConfig
{
    public static string? Write(IReadOnlyList<PluginMcpServer> servers) => Write(servers, writeEmptyExplicit: false);

    // `writeEmptyExplicit` (AC-378) exists for the headless/strict route: there, "nothing
    // resolved" must produce an actual empty `{"mcpServers":{}}` file rather than `null`, so
    // the caller can still pass `--mcp-config &lt;file&gt; --strict-mcp-config` and get a session with truly
    // zero servers. Returning `null` here — the TTY route's behaviour, and this method's default —
    // would drop `--mcp-config` from the command line entirely, and on the headless route that means the CLI
    // falls back to its own user/project config instead of the empty set the resolution actually produced: the
    // "narrowing to nothing looks like no narrowing at all" trap this ticket exists to close.
    public static string? Write(IReadOnlyList<PluginMcpServer> servers, bool writeEmptyExplicit)
    {
        var mcpServers = new JsonObject();
        foreach (var server in servers)
        {
            if (_ToEntry(server) is { } entry)
            {
                mcpServers[server.Name] = entry;
            }
        }

        if (!writeEmptyExplicit && mcpServers.Count == 0)
        {
            return null;
        }

        var root = new JsonObject { ["mcpServers"] = mcpServers };

        // Owner-only (AC-63): the user-API-key branch in `_ToEntry` puts a literal `Authorization: Bearer &lt;token&gt;`
        // in this file, and it used to land in a world-readable temp file at the umask's permissions, so any local
        // account could read a third-party token for the file's lifetime. `ClaudePrivateTempFile` owns that rule
        // for every file this plugin hands the CLI by path.
        return ClaudePrivateTempFile.Write("cockpit-claude-mcp", ".json", root.ToJsonString());
    }

    private static JsonObject? _ToEntry(PluginMcpServer server)
    {
        if (!string.IsNullOrWhiteSpace(server.Url))
        {
            var entry = new JsonObject { ["type"] = "http", ["url"] = server.Url };

            // The operator's own headers first (AC-354), so a server wanting X-Api-Key rather than a bearer works
            // here too; the host has already taken Authorization out of that set when a credential covers it.
            var headers = new JsonObject();
            foreach (var (name, value) in server.Headers)
            {
                headers[name] = value;
            }

            if (server.CockpitHosted)
            {
                // Reference the env var Claude Code expands at spawn (AC-40): ${COCKPIT_MCP_KEY}. The key never
                // lands in this file, so the config can stay a plain (world-readable) write.
                var envReference = "${" + WellKnownSessionEnvironment.CockpitMcpKey + "}";
                headers["Authorization"] = $"Bearer {envReference}";
            }
            else if (!string.IsNullOrWhiteSpace(server.BearerToken))
            {
                headers["Authorization"] = $"Bearer {server.BearerToken}";
            }

            if (headers.Count > 0)
            {
                entry["headers"] = headers;
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
