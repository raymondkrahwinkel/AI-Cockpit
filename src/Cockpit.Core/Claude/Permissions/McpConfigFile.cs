using System.Text.Json.Nodes;
using Cockpit.Core.Mcp;

namespace Cockpit.Core.Claude.Permissions;

/// <summary>
/// Builds the <c>--mcp-config</c> body every Claude-CLI session points at. It always contains the
/// cockpit's shared in-process HTTP permission server, e.g.
/// <c>{"mcpServers":{"cockpit":{"type":"http","url":"http://127.0.0.1:&lt;port&gt;/mcp"}}}</c>, and — for the
/// fan-out (#26) — merges in the user's shared MCP registry so the CLI sees the same servers the local-LLM
/// tool-loop hosts. Combined with <c>--strict-mcp-config</c> this is the CLI's <em>complete</em> server set,
/// so the cockpit registry is the single source of truth. Pure string/JSON generation so it is
/// unit-testable; writing it to disk is the host's job.
/// </summary>
public static class McpConfigFile
{
    /// <summary>The server key; the tool is addressed as <c>mcp__cockpit__permission_prompt</c>.</summary>
    public const string ServerName = "cockpit";

    /// <summary>Serializes the config JSON for the permission server alone (no registry fan-out).</summary>
    public static string Serialize(string mcpUrl) => Serialize(mcpUrl, []);

    /// <summary>
    /// Serializes the permission server plus every enabled registry server, mapped to the CLI's
    /// <c>mcpServers</c> shape. A registry server that collides with the reserved <see cref="ServerName"/>
    /// key, or that carries no usable transport target, is skipped so it can never clobber the permission
    /// entry or emit a malformed spawn config.
    /// </summary>
    public static string Serialize(string mcpUrl, IEnumerable<McpServerConfig> registryServers)
    {
        if (string.IsNullOrWhiteSpace(mcpUrl))
        {
            throw new ArgumentException("MCP url must be provided.", nameof(mcpUrl));
        }

        var servers = new JsonObject
        {
            [ServerName] = new JsonObject
            {
                ["type"] = "http",
                ["url"] = mcpUrl,
            },
        };

        foreach (var server in registryServers)
        {
            // Skip disabled servers, the reserved permission-server key, and anything scoped to local models
            // only (#26 scoping) — Claude Code has its own file/shell/web tools, so those would be noise.
            if (!server.Enabled
                || server.Scope == McpServerScope.LocalOnly
                || string.Equals(server.Name, ServerName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (_ToConfigEntry(server) is { } entry)
            {
                servers[server.Name] = entry;
            }
        }

        return new JsonObject { ["mcpServers"] = servers }.ToJsonString();
    }

    /// <summary>
    /// Serializes an mcp-config body of <em>only</em> the enabled, Claude-eligible registry servers — with no
    /// cockpit permission server. For the interactive TTY spawn (#9), which handles permission prompts in the
    /// TUI itself (so it needs no <c>--permission-prompt-tool</c>) but should still see the shared registry's
    /// servers (#26). Returns <see langword="null"/> when no registry server is eligible, so the caller can skip
    /// <c>--mcp-config</c> entirely instead of passing an empty set (and, without <c>--strict-mcp-config</c>,
    /// leave the CLI's own user/project MCP config untouched).
    /// </summary>
    public static string? SerializeRegistryOnly(IEnumerable<McpServerConfig> registryServers)
    {
        var servers = new JsonObject();
        foreach (var server in registryServers)
        {
            // Same exclusions as the fan-out above: disabled, the reserved permission-server key, and
            // local-model-only servers (Claude Code has its own file/shell/web tools, so those are noise).
            if (!server.Enabled
                || server.Scope == McpServerScope.LocalOnly
                || string.Equals(server.Name, ServerName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (_ToConfigEntry(server) is { } entry)
            {
                servers[server.Name] = entry;
            }
        }

        return servers.Count == 0 ? null : new JsonObject { ["mcpServers"] = servers }.ToJsonString();
    }

    // Maps one registry server to the CLI's mcpServers entry. Stdio → command/args; HTTP → type/url with an
    // Authorization header for a static API key. OAuth-protected HTTP servers carry only their url — the
    // static config can't hold a token, so the CLI must negotiate auth itself (headless spawns can't, so
    // those stay best-effort until the OAuth increment). Servers missing their transport target are dropped.
    private static JsonObject? _ToConfigEntry(McpServerConfig server) => server.Transport switch
    {
        McpTransport.Stdio when !string.IsNullOrWhiteSpace(server.Command) => new JsonObject
        {
            ["type"] = "stdio",
            ["command"] = server.Command,
            ["args"] = new JsonArray([.. server.Args.Select(arg => (JsonNode)arg!)]),
        },
        McpTransport.Http when !string.IsNullOrWhiteSpace(server.Url) => _HttpEntry(server),
        _ => null,
    };

    private static JsonObject _HttpEntry(McpServerConfig server)
    {
        var entry = new JsonObject
        {
            ["type"] = "http",
            ["url"] = server.Url,
        };

        if (server.Auth == McpServerAuth.ApiKey && !string.IsNullOrWhiteSpace(server.ApiKey))
        {
            entry["headers"] = new JsonObject { ["Authorization"] = $"Bearer {server.ApiKey}" };
        }

        return entry;
    }
}
