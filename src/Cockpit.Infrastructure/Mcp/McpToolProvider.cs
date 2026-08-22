using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Core.Mcp;
using Cockpit.Core.Sessions.Permissions;

namespace Cockpit.Infrastructure.Mcp;

// `IMcpToolProvider` that connects to each enabled server in the shared registry via the MCP
// client (#26), skipping unreachable ones; OAuth servers go through `IMcpOAuthAuthorizer`.
// Also the app's own `IMcpToolInvoker` (AC-502), same connect path for one tool on one server.
internal sealed class McpToolProvider(
    IMcpServerCatalog catalog,
    IMcpOAuthAuthorizer oauthAuthorizer,
    IMcpOAuthCoordinator oauthCoordinator,
    McpAuthKey authKey,
    SessionMcpKeyring keyring,
    ILogger<McpToolProvider> logger,
    IWorktreeManager? worktreeManager = null)
    : IMcpToolProvider, IMcpToolInvoker, ISingletonService
{
    public async Task<IMcpToolSession> ConnectAsync(IReadOnlySet<string>? enabledServerNames = null, string? paneId = null, string? confineFileToolsToDirectory = null, string? projectId = null, string? workingDirectory = null, CancellationToken cancellationToken = default)
    {
        // AC-89: a session with a pane id mints one per-session token here (not per server) so every
        // cockpit-hosted endpoint it connects to can attribute requests to this pane; no pane id falls back to the shared app key.
        var sessionToken = string.IsNullOrEmpty(paneId) ? null : keyring.TokenFor(paneId);
        // AC-11/AC-218: registry plus what active plugins provide, scoped to projectId so a local model
        // sees a plugin's/project's own servers too, not just the unscoped registry.
        var registry = await catalog.GetServersForProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        // AC-869: cockpit-github-pull-requests is Internal (hidden from every picker); a git-repo working
        // directory names it explicitly here rather than through operator config.
        var autoMounted = await GitHubPullRequestsAutoMount.NamesAsync(worktreeManager, workingDirectory, cancellationToken).ConfigureAwait(false);
        var effectiveSelection = McpServerRegistryFilter.WithAutoMountedServers(enabledServerNames, registry, autoMounted);
        var sessionRegistry = McpServerRegistryFilter.ApplySessionSelection(registry, effectiveSelection);
        var clients = new List<McpClient>();
        var tools = new List<McpSessionTool>();
        var connectedNames = new List<string>();
        var toolClasses = new Dictionary<string, ToolPermissionClass>(StringComparer.Ordinal);

        // Local models host the built-in defaults (#26) overlaid by the registry (a registry entry, including
        // a disabled one, overrides the same-named built-in). #44's per-session selection is already applied above.
        var enabledServers = _EffectiveServers(sessionRegistry).Where(server => server.Enabled).ToList();

        // AC-174: confined sessions swap the whole effective set for a safe one — filesystem preset
        // re-rooted at the directory, benign in-process servers, and the Autopilot report endpoint — so a
        // local model cannot reach the operator's real checkout via any other escape channel.
        if (!string.IsNullOrWhiteSpace(confineFileToolsToDirectory))
        {
            enabledServers = _ConfinedServers(enabledServers, confineFileToolsToDirectory);
        }

        // Connect concurrently rather than one-by-one — sequential round-trips added up once more than one
        // server was configured. Task.WhenAll preserves input order, so results stay deterministic even
        // though the connects race in parallel; each keeps its own try/catch (_ConnectServerAsync).
        var connections = await Task.WhenAll(enabledServers.Select(server => _ConnectServerAsync(server, sessionToken, cancellationToken)));
        var serversNeedingSignIn = new List<string>();
        var connectionIssues = new List<McpServerConnectionIssue>();

        for (var i = 0; i < connections.Length; i++)
        {
            var (connection, failureReason) = connections[i];
            if (connection is null)
            {
                // AC-500: a failed OAuth server is a named outcome ("waiting on a sign-in"), not just an absence
                // from ConnectedServerNames indistinguishable from any other unreachable/misconfigured server.
                if (enabledServers[i].Auth == McpServerAuth.OAuth)
                {
                    serversNeedingSignIn.Add(enabledServers[i].Name);
                    connectionIssues.Add(new McpServerConnectionIssue(enabledServers[i].Name, "Needs a sign-in."));
                }
                else if (failureReason is { Length: > 0 })
                {
                    // AC-997: the same outcome for every other connect failure — unreachable, or a stdio server
                    // that started and then exited — so a caller can report it upstream instead of leaving it in
                    // cockpit.log alone.
                    connectionIssues.Add(new McpServerConnectionIssue(enabledServers[i].Name, failureReason));
                }

                continue;
            }

            clients.Add(connection.Client);
            // AC-963: carry the origin server (and whether it is always mounted) alongside each tool — the search
            // layer needs it to name where a hit lives, and the driver to decide what stays preloaded.
            tools.AddRange(connection.Tools.Select(tool => new McpSessionTool(tool, connection.Name, enabledServers[i].AlwaysMounted)));
            connectedNames.Add(connection.Name);

            // AC-79: the delegated gate trusts by bare tool name, so a name shared by two servers is
            // ambiguous here. Reconcile to the *more restrictive* class on collision, never last-wins.
            foreach (var (toolName, toolClass) in connection.ToolClasses)
            {
                toolClasses[toolName] = toolClasses.TryGetValue(toolName, out var existing)
                    ? DelegatedToolPermissionPolicy.MoreRestrictive(existing, toolClass)
                    : toolClass;
            }
        }

        // AC-143: hand the session the pane/token it minted above so its own DisposeAsync can revoke exactly that
        // token when this tool loop ends — the same mint site owns the teardown, rather than a shared cross-
        // component path that could revoke a live sibling's token.
        return new McpToolSession(clients, tools, connectedNames, serversNeedingSignIn, connectionIssues, toolClasses, keyring, paneId, sessionToken);
    }

    public async Task<IReadOnlyList<AIFunction>?> EnumerateServerToolsAsync(string serverName, string? projectId = null, CancellationToken cancellationToken = default)
    {
        var registry = await catalog.GetServersForProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
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
        var (connection, _) = await _ConnectServerAsync(server, sessionToken: null, cancellationToken).ConfigureAwait(false);
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

    public async Task<McpToolInvocationResult> InvokeAsync(
        string serverName,
        string toolName,
        IReadOnlyDictionary<string, object?>? arguments = null,
        string? projectId = null,
        IReadOnlyList<McpServerConfig>? callerFallbackServers = null,
        CancellationToken cancellationToken = default)
    {
        var registry = await catalog.GetServersForProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        var server = registry.FirstOrDefault(candidate =>
            candidate.Enabled && string.Equals(candidate.Name, serverName, StringComparison.OrdinalIgnoreCase));

        // AC-499: the catalog only carries a plugin-delivered server once a project points at it. A caller
        // entitled to that server (scoped by the host, see ICockpitHost.CallMcpToolAsync) can still reach it via
        // callerFallbackServers — the same asymmetry CockpitHost's acceptance check already tolerates.
        server ??= callerFallbackServers?.FirstOrDefault(candidate =>
            candidate.Enabled && string.Equals(candidate.Name, serverName, StringComparison.OrdinalIgnoreCase));

        if (server is null)
        {
            return McpToolInvocationResult.Failed($"No enabled MCP server named \"{serverName}\".");
        }

        // Same AC-134 rule EnumerateServerToolsAsync follows: never pop an interactive browser sign-in from this
        // path — report the named outcome instead, so a caller can offer its own "sign in" action.
        if (server.Auth == McpServerAuth.OAuth
            && await oauthCoordinator.GetStateAsync(server, cancellationToken).ConfigureAwait(false) == McpAuthState.AuthorizationRequired)
        {
            return McpToolInvocationResult.AuthorizationRequired;
        }

        var (connection, _) = await _ConnectServerAsync(server, sessionToken: null, cancellationToken).ConfigureAwait(false);
        if (connection is null)
        {
            return McpToolInvocationResult.Failed($"Could not connect to \"{serverName}\".");
        }

        try
        {
            var result = await connection.Client.CallToolAsync(toolName, arguments, cancellationToken: cancellationToken).ConfigureAwait(false);
            var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));

            return result.IsError == true
                ? McpToolInvocationResult.Failed(text.Length > 0 ? text : $"\"{toolName}\" reported an error.")
                : McpToolInvocationResult.Success(text);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "MCP tool {Tool} on {Server} could not be called", toolName, serverName);
            return McpToolInvocationResult.Failed(exception.Message);
        }
        finally
        {
            await connection.Client.DisposeAsync().ConfigureAwait(false);
        }
    }

    // The connect result plus, on failure, the operator-facing reason (AC-997) — carried alongside the null
    // ServerConnection rather than thrown, since Task.WhenAll below needs one result per server regardless of
    // which one failed.
    private async Task<(ServerConnection? Connection, string? FailureReason)> _ConnectServerAsync(McpServerConfig server, string? sessionToken, CancellationToken cancellationToken)
    {
        try
        {
            // AC-505 follow-up: the widened timeout is only worth paying when a sign-in might actually run —
            // GetStateAsync is a local read (no network/browser), so an already-usable token still connects fast.
            var needsInteractiveOAuth = server.Auth == McpServerAuth.OAuth
                && await oauthCoordinator.GetStateAsync(server, cancellationToken).ConfigureAwait(false) == McpAuthState.AuthorizationRequired;
            var clientOptions = needsInteractiveOAuth ? McpInteractiveOAuthClientOptions.Create() : null;
            var client = await McpClientConnector.ConnectAsync(_BuildTransport(server, sessionToken), clientOptions, cancellationToken).ConfigureAwait(false);
            var serverTools = await client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            // AC-79: classify each tool from its MCP annotations at connect; an absent readOnlyHint stays
            // Unknown, never "safe". Preset identified by npm package (AC-100): the name-based fallback below
            // must never fire for an arbitrary server that happens to expose write_file/read_file.
            var isFilesystemPreset = server.Args.Any(arg => arg.Contains(McpServerPresets.FilesystemServerPackage, StringComparison.OrdinalIgnoreCase));

            var classes = new Dictionary<string, ToolPermissionClass>(StringComparer.Ordinal);
            foreach (var tool in serverTools)
            {
                var annotations = tool.ProtocolTool.Annotations;
                var annotationClass = DelegatedToolPermissionPolicy.Classify(annotations?.ReadOnlyHint, annotations?.DestructiveHint);

                // AC-100/AC-112: the built-in filesystem preset ships no hints, so its write_file is Unknown
                // and would get blocked. Fall back to first-party name knowledge, but ONLY for that preset and
                // ONLY where the hint is Unknown — any explicit hint always wins; a rogue server gets no such treatment.
                classes[tool.Name] = annotationClass == ToolPermissionClass.Unknown && isFilesystemPreset
                    ? DelegatedToolPermissionPolicy.ClassifyWellKnown(tool.Name) ?? annotationClass
                    : annotationClass;
            }

            return (new ServerConnection(client, [.. serverTools], server.Name, classes), null);
        }
        catch (Exception ex) when (server.Auth == McpServerAuth.OAuth)
        {
            // Same idiom as McpOAuthCoordinator's non-interactive handshake: with nobody here to answer a browser
            // prompt, any failure at this transport reads as "no usable sign-in yet" rather than a specific status
            // code — the SDK's own OAuth negotiation can fail several ways before it ever gets as far as one.
            logger.LogWarning(ex, "MCP server {Name} needs an OAuth sign-in that has not happened yet; skipping its tools", server.Name);
            return (null, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "MCP server {Name} could not be connected — skipping its tools", server.Name);
            return (null, _ShortReason(ex));
        }
    }

    // The operator-facing reason (AC-997): the exception's own message, one line, no stack trace — never
    // ex.ToString(), which would carry both.
    private static string _ShortReason(Exception ex) => ex.Message.Split('\n')[0].Trim();

    // One server's successful connect result: the live client (kept for disposal), its tools, their permission classes, and its name.
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

    // Pane-scoped Autopilot control endpoints a confined session still needs (control-only, no file/command
    // access, so safe). A session only ever selects one of the two. Named as literals to keep
    // Infrastructure independent of the Autopilot plugin; kept in sync with AutopilotRunTools/AutopilotCeoTools.EndpointName.
    private static readonly HashSet<string> ConfinedControlEndpoints =
        new(StringComparer.OrdinalIgnoreCase) { "cockpit-autopilot-run", "cockpit-autopilot-ceo" };

    // AC-174: the confined effective set for a session pinned to <paramref name="root"/> — filesystem preset
    // re-rooted at the worktree, the in-memory knowledge server, and the Autopilot endpoint if already present.
    // Built from the presets, not the caller's own servers, so a custom same-named "filesystem" cannot widen the sandbox.
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

        confined.AddRange(effective.Where(server => ConfinedControlEndpoints.Contains(server.Name)));
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
        // AC-792: pinned to one certificate when this server is a paired Cockpit node, untouched otherwise.
        McpTransport.Http => NodeCertificatePin.TransportFor(server, new HttpClientTransportOptions
        {
            Name = server.Name,
            Endpoint = new Uri(server.Url ?? string.Empty),
            TransportMode = HttpTransportMode.AutoDetect,
            // AC-40/AC-89: a bearer header carries the auth for a cockpit-hosted endpoint (this session's
            // per-session token, else the shared app key) or a user API-key server's own key; OAuth via the authorizer.
            // AC-354: operator headers first, then the auth-derived Authorization on top.
            AdditionalHeaders = _Headers(server, sessionToken),
            // Interactive: this transport is built for a session the operator started, which is a moment they may be
            // asked to sign in. The pre-flight tool count never reaches here — EnumerateServerToolsAsync returns
            // early for an OAuth server precisely so counting tokens cannot open a browser (AC-134).
            OAuth = server.Auth == McpServerAuth.OAuth ? oauthAuthorizer.CreateOptions(server, interactive: true) : null,
        }),
        _ => throw new NotSupportedException($"Unsupported MCP transport {server.Transport}."),
    };

    // Same operator-headers-vs-managed-credential rule the spawn paths use (McpAgentHeaders); here the cockpit
    // sets the Authorization itself in-process instead of a spawned agent's provider writing it.
    private Dictionary<string, string> _Headers(McpServerConfig server, string? sessionToken)
    {
        var bearer = server.CockpitHosted && sessionToken is not null
            ? sessionToken
            : CockpitMcpBearer.For(server, authKey);

        var headers = new Dictionary<string, string>(McpAgentHeaders.For(server, bearer), StringComparer.OrdinalIgnoreCase);
        if (bearer is not null)
        {
            headers["Authorization"] = $"Bearer {bearer}";
        }

        return headers;
    }

    // AC-143: keyring/paneId/token are the mint this session made in ConnectAsync (null when there was no pane id
    // to mint for), carried through so DisposeAsync can revoke exactly that token at this route's own teardown —
    // the in-process tool loop ending is the only signal this component has that the pane is done with it.
    private sealed class McpToolSession(
        IReadOnlyList<McpClient> clients,
        IReadOnlyList<McpSessionTool> tools,
        IReadOnlyList<string> names,
        IReadOnlyList<string> serversNeedingSignIn,
        IReadOnlyList<McpServerConnectionIssue> connectionIssues,
        IReadOnlyDictionary<string, ToolPermissionClass> toolClasses,
        SessionMcpKeyring? keyring = null,
        string? paneId = null,
        string? token = null)
        : IMcpToolSession
    {
        public IReadOnlyList<McpSessionTool> Tools => tools;

        public IReadOnlyList<string> ConnectedServerNames => names;

        public IReadOnlyList<string> ServersNeedingSignIn => serversNeedingSignIn;

        public IReadOnlyList<McpServerConnectionIssue> ConnectionIssues => connectionIssues;

        public IReadOnlyDictionary<string, ToolPermissionClass> ToolClasses => toolClasses;

        public string? PaneToken => token;

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

            // AC-143: this pane's bearer must not survive the tool loop that owned it — dropped by the minter,
            // never logged (the value is the secret).
            if (keyring is not null && paneId is not null && token is not null)
            {
                keyring.Revoke(paneId, token);
            }
        }
    }
}
