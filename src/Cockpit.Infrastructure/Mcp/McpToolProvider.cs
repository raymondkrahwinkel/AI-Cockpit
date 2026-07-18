using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Cockpit.Core.Sessions.Permissions;

namespace Cockpit.Infrastructure.Mcp;

/// <summary>
/// <see cref="IMcpToolProvider"/> that connects to each enabled server in the shared registry via the MCP
/// client (stdio or streamable-HTTP) and collects their tools (#26). A server that fails to start or is
/// unreachable is logged and skipped, so the session runs with whatever connected rather than failing.
/// OAuth-protected HTTP servers go through <see cref="IMcpOAuthAuthorizer"/> (loopback + system browser), so
/// the first tool use pops a browser sign-in and the SDK handles PKCE, discovery and token refresh.
/// </summary>
internal sealed class McpToolProvider(IMcpServerCatalog catalog, IMcpOAuthAuthorizer oauthAuthorizer, McpAuthKey authKey, ILogger<McpToolProvider> logger)
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
        var toolClasses = new Dictionary<string, ToolPermissionClass>(StringComparer.Ordinal);

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

            // A later server's tool of the same name overwrites an earlier one's class, matching the tool list's
            // own last-wins behaviour when two servers expose the same tool name.
            foreach (var (toolName, toolClass) in connection.ToolClasses)
            {
                toolClasses[toolName] = toolClass;
            }
        }

        return new McpToolSession(clients, tools, connectedNames, toolClasses);
    }

    private async Task<ServerConnection?> _ConnectServerAsync(McpServerConfig server, CancellationToken cancellationToken)
    {
        try
        {
            var client = await McpClient.CreateAsync(_BuildTransport(server), cancellationToken: cancellationToken).ConfigureAwait(false);
            var serverTools = await client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            // Classify each tool from its MCP annotations (AC-79) at connect, while we still have the typed
            // McpClientTool — the delegated gate later reads these by tool name. Annotations are advisory hints,
            // so an absent readOnlyHint stays Unknown (trusted only via the profile allow-list), not "safe".
            var classes = new Dictionary<string, ToolPermissionClass>(StringComparer.Ordinal);
            foreach (var tool in serverTools)
            {
                var annotations = tool.ProtocolTool.Annotations;
                classes[tool.Name] = DelegatedToolPermissionPolicy.Classify(annotations?.ReadOnlyHint, annotations?.DestructiveHint);
            }

            return new ServerConnection(client, [.. serverTools], server.Name, classes);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "MCP server {Name} could not be connected — skipping its tools", server.Name);
            return null;
        }
    }

    /// <summary>One server's successful connect result: the live client (kept for disposal), its tools, their permission classes, and its name.</summary>
    private sealed record ServerConnection(McpClient Client, IReadOnlyList<AIFunction> Tools, string Name, IReadOnlyDictionary<string, ToolPermissionClass> ToolClasses);

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
            // A bearer header carries this run's key for a cockpit-hosted endpoint (AC-40) or a user API-key server's
            // own key; OAuth is negotiated by the SDK via the authorizer.
            AdditionalHeaders = CockpitMcpBearer.For(server, authKey) is { } bearer
                ? new Dictionary<string, string> { ["Authorization"] = $"Bearer {bearer}" }
                : new Dictionary<string, string>(),
            OAuth = server.Auth == McpServerAuth.OAuth ? oauthAuthorizer.CreateOptions(server) : null,
        }),
        _ => throw new NotSupportedException($"Unsupported MCP transport {server.Transport}."),
    };

    private sealed class McpToolSession(IReadOnlyList<McpClient> clients, IReadOnlyList<AIFunction> tools, IReadOnlyList<string> names, IReadOnlyDictionary<string, ToolPermissionClass> toolClasses)
        : IMcpToolSession
    {
        public IReadOnlyList<AIFunction> Tools => tools;

        public IReadOnlyList<string> ConnectedServerNames => names;

        public IReadOnlyDictionary<string, ToolPermissionClass> ToolClasses => toolClasses;

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
