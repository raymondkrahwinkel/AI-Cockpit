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
internal sealed class McpToolProvider(IMcpServerCatalog catalog, IMcpOAuthAuthorizer oauthAuthorizer, McpAuthKey authKey, SessionMcpKeyring keyring, ILogger<McpToolProvider> logger)
    : IMcpToolProvider, ISingletonService
{
    public async Task<IMcpToolSession> ConnectAsync(IReadOnlySet<string>? enabledServerNames = null, string? paneId = null, string? confineFileToolsToDirectory = null, CancellationToken cancellationToken = default)
    {
        // AC-89: when this in-process tool loop belongs to a session with a pane id (a local-model session), mint it
        // one per-session token — used for every cockpit-hosted endpoint it connects to — so those endpoints can
        // attribute its requests to this pane and the consent broker scopes on the real session, not the id the model
        // declares. Minted once here, not per server, so the concurrent connects below all present the same live
        // token. No pane id falls back to the shared app key.
        var sessionToken = string.IsNullOrEmpty(paneId) ? null : keyring.TokenFor(paneId);
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

        // Confinement (AC-174, Raymond 2026-07-22): when the session is confined to a directory, replace the whole
        // effective set with a safe one so a local model cannot reach the operator's real checkout — the file-capable
        // servers become the built-in filesystem preset re-rooted at that directory (never a custom same-named registry
        // server, which is not trusted to sandbox), plus benign in-process servers, plus the pane-scoped Autopilot report
        // endpoint the step needs. Every escape channel (a shell/terminal, an orchestrator that spawns unconfined
        // sessions, worktree tools, any other filesystem) is dropped regardless of what the selection asked for.
        if (!string.IsNullOrWhiteSpace(confineFileToolsToDirectory))
        {
            enabledServers = _ConfinedServers(enabledServers, confineFileToolsToDirectory);
        }

        // Connect every enabled server concurrently rather than one-by-one — sequential connect + list-tools
        // round-trips added up badly once more than one server was configured. Each connect keeps its own
        // try/catch (in _ConnectServerAsync), so a server that fails or is unreachable is still skipped without
        // blocking — or now, delaying — the others. Task.WhenAll returns its results in the same order as the
        // input sequence regardless of which task finishes first, so the resulting tools/connected-names lists
        // stay in the same (deterministic) order as enabledServers even though the connects race in parallel.
        var connections = await Task.WhenAll(enabledServers.Select(server => _ConnectServerAsync(server, sessionToken, cancellationToken)));

        foreach (var connection in connections)
        {
            if (connection is null)
            {
                continue;
            }

            clients.Add(connection.Client);
            tools.AddRange(connection.Tools);
            connectedNames.Add(connection.Name);

            // Trust for the delegated gate is keyed on the bare tool name (AC-79), so a name exposed by two
            // enabled servers is ambiguous — the tool list keeps both, and which one the model resolves is not
            // decided here. Reconcile the class to the *more restrictive* of the collision rather than last-wins,
            // so a second server cannot shadow a safe name to widen what runs unattended.
            foreach (var (toolName, toolClass) in connection.ToolClasses)
            {
                toolClasses[toolName] = toolClasses.TryGetValue(toolName, out var existing)
                    ? DelegatedToolPermissionPolicy.MoreRestrictive(existing, toolClass)
                    : toolClass;
            }
        }

        return new McpToolSession(clients, tools, connectedNames, toolClasses);
    }

    public async Task<IReadOnlyList<AIFunction>?> EnumerateServerToolsAsync(string serverName, CancellationToken cancellationToken = default)
    {
        var registry = await catalog.GetServersAsync(cancellationToken).ConfigureAwait(false);
        var server = registry.FirstOrDefault(candidate =>
            candidate.Enabled && string.Equals(candidate.Name, serverName, StringComparison.OrdinalIgnoreCase));

        // Unknown/disabled server, or one whose only auth is an interactive OAuth sign-in: a pre-flight count
        // (AC-134) must neither spawn the built-in defaults nor pop a browser, so those come back "unknown".
        if (server is null || server.Auth == McpServerAuth.OAuth)
        {
            return null;
        }

        // Connect ONLY this one server — bypassing ConnectAsync/_EffectiveServers, which would overlay the built-in
        // local-default servers (filesystem/fetch/git/…) and both spawn and count them (AC-134 security review).
        var connection = await _ConnectServerAsync(server, sessionToken: null, cancellationToken).ConfigureAwait(false);
        if (connection is null)
        {
            return null;
        }

        try
        {
            return connection.Tools;
        }
        finally
        {
            await connection.Client.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<ServerConnection?> _ConnectServerAsync(McpServerConfig server, string? sessionToken, CancellationToken cancellationToken)
    {
        try
        {
            var client = await McpClient.CreateAsync(_BuildTransport(server, sessionToken), cancellationToken: cancellationToken).ConfigureAwait(false);
            var serverTools = await client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            // Classify each tool from its MCP annotations (AC-79) at connect, while we still have the typed
            // McpClientTool — the delegated gate later reads these by tool name. Annotations are advisory hints,
            // so an absent readOnlyHint stays Unknown (trusted only via the profile allow-list), not "safe".
            // Whether this IS the built-in filesystem preset, identified by its npm package rather than by the
            // server or tool name. The name-based fallback below is only sound for that one first-party server —
            // whose tools are scoped to a single configured folder — so it must never fire for an arbitrary
            // server that happens to expose a tool called write_file/read_file (AC-100 security review).
            var isFilesystemPreset = server.Args.Any(arg => arg.Contains(McpServerPresets.FilesystemServerPackage, StringComparison.OrdinalIgnoreCase));

            var classes = new Dictionary<string, ToolPermissionClass>(StringComparer.Ordinal);
            foreach (var tool in serverTools)
            {
                var annotations = tool.ProtocolTool.Annotations;
                var annotationClass = DelegatedToolPermissionPolicy.Classify(annotations?.ReadOnlyHint, annotations?.DestructiveHint);

                // The built-in filesystem preset ships no read-only/destructive hints, so its write_file is
                // Unknown and the delegated gate blocks it at every ceiling below bypassPermissions — a local
                // coder profile cannot write a file at the default acceptEdits ceiling (AC-100/AC-112). Fall back
                // to first-party knowledge of the tool by name, but ONLY (a) for that preset, and (b) where the
                // server gave no readOnlyHint at all (Unknown) — any explicit hint, true or false, is always
                // honoured and never widened. A rogue server reusing these names gets no such treatment.
                classes[tool.Name] = annotationClass == ToolPermissionClass.Unknown && isFilesystemPreset
                    ? DelegatedToolPermissionPolicy.ClassifyWellKnown(tool.Name) ?? annotationClass
                    : annotationClass;
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

    // The pane-scoped Autopilot report endpoint a confined step still needs (to call autopilot_step_done). It is
    // cockpit-hosted and control-only — it cannot write files or run commands — so it is safe inside a confined session.
    // Named as a literal to keep Infrastructure independent of the Autopilot plugin; kept in sync with
    // AutopilotRunTools.EndpointName.
    private const string ConfinedReportEndpoint = "cockpit-autopilot-run";

    // The confined effective set for a session pinned to <paramref name="root"/> (AC-174): the built-in filesystem
    // preset re-rooted at the worktree (the only file-write path a confined session gets), the built-in in-memory
    // knowledge server (benign, no disk escape), and the Autopilot report endpoint if this session already had it. Built
    // from the presets — not from the caller's own file servers — so a custom same-named "filesystem" cannot smuggle in a
    // different, wider sandbox. Everything else (the home-rooted defaults, git, fetch, a shell/terminal, an orchestrator,
    // worktree tools) is deliberately left out: none of it can reach the operator's real checkout from here.
    internal static List<McpServerConfig> _ConfinedServers(IReadOnlyList<McpServerConfig> effective, string root)
    {
        var confined = new List<McpServerConfig>();
        foreach (var preset in McpServerPresets.LocalDefaults)
        {
            if (string.Equals(preset.Name, "filesystem", StringComparison.OrdinalIgnoreCase))
            {
                confined.Add(_ReRootLastArg(preset, root));
            }
            else if (string.Equals(preset.Name, "memory", StringComparison.OrdinalIgnoreCase))
            {
                confined.Add(preset);
            }
        }

        confined.AddRange(effective.Where(server => string.Equals(server.Name, ConfinedReportEndpoint, StringComparison.OrdinalIgnoreCase)));
        return confined;
    }

    // Re-roots a filesystem-style stdio preset by replacing its last CLI argument (the server's single allowed
    // directory) with the worktree, so its sandbox is the worktree rather than the user's home folder. The filesystem
    // server sandboxes on this argument, not on the process cwd, so rewriting the arg is what actually confines it.
    private static McpServerConfig _ReRootLastArg(McpServerConfig server, string root)
    {
        if (server.Args is not { Count: > 0 })
        {
            return server;
        }

        var args = server.Args.ToArray();
        args[^1] = root;
        return server with { Args = args };
    }

    private IClientTransport _BuildTransport(McpServerConfig server, string? sessionToken) => server.Transport switch
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
            // A bearer header carries the auth for a cockpit-hosted endpoint (AC-40) or a user API-key server's own
            // key; OAuth is negotiated by the SDK via the authorizer. AC-89: a cockpit-hosted endpoint gets this
            // session's per-session token when it has one (so its requests are attributed to this pane), else the
            // shared app key.
            AdditionalHeaders = (server.CockpitHosted && sessionToken is not null ? sessionToken : CockpitMcpBearer.For(server, authKey)) is { } bearer
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
