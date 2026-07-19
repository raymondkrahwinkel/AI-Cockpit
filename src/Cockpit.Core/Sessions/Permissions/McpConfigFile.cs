using System.Text.Json.Nodes;
using Cockpit.Core.Mcp;

namespace Cockpit.Core.Sessions.Permissions;

/// <summary>
/// Builds the <c>--mcp-config</c> body a Claude-CLI session points at from the user's shared MCP registry, so the
/// CLI sees the same servers the local-LLM tool-loop hosts (#26). Combined with <c>--strict-mcp-config</c> this is
/// the CLI's <em>complete</em> server set, so the cockpit registry is the single source of truth. Pure string/JSON
/// generation so it is unit-testable; writing it to disk is the host's job.
/// <para>
/// The cockpit once injected its own in-process HTTP permission server into this body; that endpoint is gone, and
/// the host-side <c>Serialize(mcpUrl,…)</c> overloads that carried it were removed with it (AC-46) so an
/// unauthenticated permission endpoint cannot be reintroduced through a stale config path. What remains here is
/// registry fan-out only; the provider plugins build their own spawn config.
/// </para>
/// </summary>
public static class McpConfigFile
{
    /// <summary>The reserved server key the registry may never claim (it once addressed the cockpit permission server).</summary>
    public const string ServerName = "cockpit";

    /// <summary>
    /// Whether a registry server should fan out to an agentic CLI session (Claude Code, Codex) — enabled, not
    /// scoped to local models only (those agents ship their own file/shell/web tools, so a filesystem server
    /// there is noise), and not the reserved key (never surfaced from the registry). The one predicate the
    /// registry serializer and the plugin driver adapter (#26/#44) share, so "which servers a coding agent sees"
    /// lives in one place.
    /// </summary>
    public static bool IsAgentEligible(McpServerConfig server) =>
        server.Enabled
        && server.Scope != McpServerScope.LocalOnly
        && !string.Equals(server.Name, ServerName, StringComparison.OrdinalIgnoreCase);

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
            if (IsAgentEligible(server) && _ToConfigEntry(server) is { } entry)
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
