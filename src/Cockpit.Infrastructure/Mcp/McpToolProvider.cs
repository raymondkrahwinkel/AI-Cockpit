using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;

namespace Cockpit.Infrastructure.Mcp;

/// <summary>
/// <see cref="IMcpToolProvider"/> that connects to each enabled server in the shared registry via the MCP
/// client (stdio or streamable-HTTP) and collects their tools (#26). A server that fails to start or is
/// unreachable is logged and skipped, so the session runs with whatever connected rather than failing.
/// OAuth-protected HTTP servers go through <see cref="IMcpOAuthAuthorizer"/> (loopback + system browser), so
/// the first tool use pops a browser sign-in and the SDK handles PKCE, discovery and token refresh.
/// </summary>
internal sealed class McpToolProvider(IMcpServerCatalog catalog, IMcpOAuthAuthorizer oauthAuthorizer, ILogger<McpToolProvider> logger)
    : IMcpToolProvider, ISingletonService
{
    public async Task<IMcpToolSession> ConnectAsync(IReadOnlySet<string>? enabledServerNames = null, CancellationToken cancellationToken = default)
    {
        // The effective set — registry plus what active plugins provide (AC-11) — so a local model gets a
        // plugin's MCP servers too, and the per-session selection can narrow them like any other.
        var registry = await catalog.GetServersAsync(cancellationToken).ConfigureAwait(false);
        var sessionRegistry = McpServerRegistryFilter.ApplySessionSelection(registry, enabledServerNames);
        var clients = new List<McpClient>();
        var tools = new List<AIFunction>();
        var connectedNames = new List<string>();

        // Local models host the built-in defaults (filesystem etc.) plus every enabled registry server not
        // scoped to Claude only (#26). A registry entry overrides the built-in of the same name — including a
        // disabled one, which removes that default — so defaults are a baseline the user can retarget or drop.
        // The per-session selection (#44) is applied to the registry above, before this merge, so a built-in
        // default is never excluded just because it is not part of the registry-derived checklist.
        var enabledServers = _EffectiveServers(sessionRegistry).Where(server => server.Enabled).ToList();

        // Connect every enabled server concurrently rather than one-by-one — sequential connect + list-tools
        // round-trips added up badly once more than one server was configured. Each connect keeps its own
        // try/catch (in _ConnectServerAsync), so a server that fails or is unreachable is still skipped without
        // blocking — or now, delaying — the others. Task.WhenAll returns its results in the same order as the
        // input sequence regardless of which task finishes first, so the resulting tools/connected-names lists
        // stay in the same (deterministic) order as enabledServers even though the connects race in parallel.
        var connections = await Task.WhenAll(enabledServers.Select(server => _ConnectServerAsync(server, cancellationToken)));

        foreach (var connection in connections)
        {
            if (connection is null)
            {
                continue;
            }

            clients.Add(connection.Client);
            tools.AddRange(connection.Tools);
            connectedNames.Add(connection.Name);
        }

        return new McpToolSession(clients, tools, connectedNames);
    }

    private async Task<ServerConnection?> _ConnectServerAsync(McpServerConfig server, CancellationToken cancellationToken)
    {
        try
        {
            var client = await McpClient.CreateAsync(_BuildTransport(server), cancellationToken: cancellationToken).ConfigureAwait(false);
            var serverTools = await client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return new ServerConnection(client, [.. serverTools], server.Name);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "MCP server {Name} could not be connected — skipping its tools", server.Name);
            return null;
        }
    }

    /// <summary>One server's successful connect result: the live client (kept for disposal), its tools, and its name.</summary>
    private sealed record ServerConnection(McpClient Client, IReadOnlyList<AIFunction> Tools, string Name);

    // Built-in local defaults, overlaid with the registry: a registry server (that is not Claude-only)
    // replaces the built-in of the same name, so the user can retarget filesystem or drop a default by
    // disabling a same-named entry. Registry-only servers (All/LocalOnly scope) are added as well.
    internal static IReadOnlyList<McpServerConfig> _EffectiveServers(IReadOnlyList<McpServerConfig> registry)
    {
        var byName = new Dictionary<string, McpServerConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var server in McpServerPresets.LocalDefaults)
        {
            byName[server.Name] = server;
        }

        foreach (var server in registry.Where(server => server.Scope != McpServerScope.ClaudeOnly))
        {
            byName[server.Name] = server;
        }

        return [.. byName.Values];
    }

    private IClientTransport _BuildTransport(McpServerConfig server) => server.Transport switch
    {
        McpTransport.Stdio => new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = server.Name,
            Command = server.Command ?? string.Empty,
            Arguments = [.. server.Args],
            EnvironmentVariables = StdioServerEnvironment.Build(),
        }),
        McpTransport.Http => new HttpClientTransport(new HttpClientTransportOptions
        {
            Name = server.Name,
            Endpoint = new Uri(server.Url ?? string.Empty),
            TransportMode = HttpTransportMode.AutoDetect,
            // A static API key rides as a bearer header; OAuth is negotiated by the SDK via the authorizer.
            AdditionalHeaders = server.Auth == McpServerAuth.ApiKey && !string.IsNullOrWhiteSpace(server.ApiKey)
                ? new Dictionary<string, string> { ["Authorization"] = $"Bearer {server.ApiKey}" }
                : new Dictionary<string, string>(),
            OAuth = server.Auth == McpServerAuth.OAuth ? oauthAuthorizer.CreateOptions(server) : null,
        }),
        _ => throw new NotSupportedException($"Unsupported MCP transport {server.Transport}."),
    };

    private sealed class McpToolSession(IReadOnlyList<McpClient> clients, IReadOnlyList<AIFunction> tools, IReadOnlyList<string> names)
        : IMcpToolSession
    {
        public IReadOnlyList<AIFunction> Tools => tools;

        public IReadOnlyList<string> ConnectedServerNames => names;

        public async ValueTask DisposeAsync()
        {
            foreach (var client in clients)
            {
                try
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Best-effort teardown — a client that already died on its own is fine.
                }
            }
        }
    }
}
