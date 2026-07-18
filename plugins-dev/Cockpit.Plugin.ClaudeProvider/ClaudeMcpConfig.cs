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
        return _WritePrivate(root.ToJsonString());
    }

    /// <summary>
    /// Writes the mcp-config owner-only (AC-63). The user-API-key branch in <see cref="_ToEntry"/> puts a literal
    /// <c>Authorization: Bearer &lt;token&gt;</c> in this file; it used to land in a world-readable temp file at the
    /// umask's permissions, so any local account could read a third-party token for the file's lifetime. The file
    /// (and its directory) are now 0600/0700 on Unix, set at create time so there is no window at the umask; on
    /// Windows the per-user temp profile is the protection, exactly as the host's <c>TtyMcpConfigFile</c> /
    /// <c>CockpitConfigPath</c> treat it (this plugin cannot reference Infrastructure, so it mirrors the pattern).
    /// </summary>
    private static string _WritePrivate(string json)
    {
        var directory = Path.Combine(Path.GetTempPath(), "cockpit-claude-mcp");
        Directory.CreateDirectory(directory);
        _Restrict(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var path = Path.Combine(directory, $"{Guid.NewGuid():N}.json");
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            // Set at create time, so the file never exists at the umask's permissions with the token already in it.
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(path, options))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(json);
        }

        _Restrict(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return path;
    }

    // A no-op on Windows, which has no Unix mode bits — there the per-user temp profile is the protection. Guarding
    // inside the method (not at the call site) is what keeps the SetUnixFileMode call off the Windows analysis path.
    private static void _Restrict(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, mode);
        }
        catch (Exception)
        {
            // A filesystem that carries no Unix permissions (a mounted share, a container volume) is not a reason to
            // fail the launch — the write is what matters, and UnixCreateMode already set the mode where it is honoured.
        }
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
