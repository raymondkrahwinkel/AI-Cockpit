using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Delegation;
using Cockpit.Core.Mcp;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Sessions;
using Cockpit.Core.Sessions.Permissions;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Plugins.Abstractions.Sessions;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.Sessions.Tty;

/// <summary>
/// Runs a plugin's <see cref="IPluginTtyProvider"/> as one of the cockpit's own <see cref="ITtySessionProvider"/>s.
/// The two contracts say the same thing in two vocabularies — the plugin SDK cannot reference the core's types
/// without binding every plugin to the core's version of them — so this is where one becomes the other.
/// </summary>
/// <remarks>
/// The host resolves the things a plugin cannot reach across the isolation boundary and hands them through the
/// grown context (Fase 4): the shared MCP registry (#26) and whether the orchestrator is enabled (#67), so a rich
/// TUI like Claude can fan the registry into <c>--mcp-config</c> and append the delegation prompt. The status
/// snapshot file the provider names in its spec is carried back to the core spec, so the session header still
/// polls the provider's limits.
/// </remarks>
internal sealed class PluginTtySessionProviderAdapter(
    string providerId,
    IPluginTtyProvider inner,
    string configJson,
    IMcpServerCatalog? mcpServerCatalog = null,
    IMcpOAuthCoordinator? oauthCoordinator = null,
    ILogger<PluginTtySessionProviderAdapter>? logger = null) : ITtySessionProvider
{
    public string ProviderId => providerId;

    public TtyLaunchSpec BuildLaunch(TtyLaunchContext context)
    {
        var (mcpServers, canDelegate) = _ResolveRegistry(context.EnabledMcpServerNames, context.ProjectId);

        // The base environment is handed straight through: the host (TtyLauncher) has already put this run's MCP
        // auth key on it (COCKPIT_MCP_KEY, AC-40) so a cockpit-hosted server's config can reference the env var
        // rather than embed a literal — it is not the adapter's to add, only to relay.
        var spec = inner.BuildLaunch(new PluginTtyLaunchContext(
            configJson,
            context.Options,
            context.WorkingDirectory,
            _Resume(context.Resume),
            context.BaseEnvironment)
        {
            McpServers = mcpServers,
            DelegationSystemPrompt = canDelegate ? DelegationSystemPrompt.Default : null,
        });

        return new TtyLaunchSpec(
            spec.ExecutablePath,
            spec.Arguments,
            spec.EnvironmentOverlay,
            spec.WorkingDirectory,
            spec.SessionScopedFiles,
            spec.StatusFile);
    }

    /// <summary>
    /// The agent-eligible MCP servers and whether the orchestrator is enabled — read once per launch. The
    /// per-session selection (#44) narrows the eligible registry to the operator's checklist first (an unchecked
    /// server never reaches the CLI), exactly as the SDK route's <c>PluginSessionDriverAdapter</c> does; a
    /// <see langword="null"/> selection means no narrowing. Delegation is judged on the <em>selected</em> set: if
    /// the operator unchecked the orchestrator server, this session cannot delegate. Sync (the spawn path is
    /// synchronous) and best-effort: no store (a unit test wiring none) or a read failure means no servers and no
    /// delegation, rather than blocking the launch. <paramref name="projectId"/> (AC-218) scopes the registry read
    /// to that project's own view — resolved host-side into the launch context, never a paneId→projectId lookup
    /// here (that would need a UI-thread hop this synchronous spawn path cannot take).
    /// </summary>
    private (IReadOnlyList<PluginMcpServer> McpServers, bool CanDelegate) _ResolveRegistry(IReadOnlySet<string>? enabledServerNames, string? projectId)
    {
        if (mcpServerCatalog is null)
        {
            return ([], false);
        }

        try
        {
            var registry = mcpServerCatalog.GetServersForProjectAsync(projectId).GetAwaiter().GetResult();
            var selected = McpServerRegistryFilter.ApplySessionSelection(registry, enabledServerNames);
            var servers = selected
                .Where(McpConfigFile.IsAgentEligible)
                .Select(server => _ToPluginMcpServer(server, _AcquireCredential(server)))
                .OfType<PluginMcpServer>()
                .ToList();
            var canDelegate = selected.Any(server =>
                server.Enabled && string.Equals(server.Name, DelegationMcp.ServerName, StringComparison.OrdinalIgnoreCase));
            return (servers, canDelegate);
        }
        catch (Exception)
        {
            return ([], false);
        }
    }

    /// <summary>
    /// The credential this launch presents to <paramref name="server"/> (AC-353). Asked for non-interactively, so a
    /// launch never makes a browser window appear on its own; a token that cannot be renewed leaves the server
    /// unauthorized and says so, rather than the CLI meeting a 401 later with no way to act on it.
    /// <para>
    /// Blocking, like the registry read a few lines up and for the same reason: this spawn path is synchronous all
    /// the way out to <c>ITtyLauncher.Launch</c>. Only reached for an OAuth server, and only doing I/O when a stored
    /// token has actually gone stale, so the ordinary launch neither blocks nor touches the network.
    /// </para>
    /// </summary>
    private string? _AcquireCredential(McpServerConfig server)
    {
        if (oauthCoordinator is null || server.Auth != McpServerAuth.OAuth)
        {
            return null;
        }

        var access = oauthCoordinator.AcquireAsync(server, interactive: false).GetAwaiter().GetResult();
        if (access.State == McpAuthState.AuthorizationRequired)
        {
            logger?.LogWarning(
                "MCP server {Name} needs an authorization the cockpit does not hold; the session starts without its tools. Sign in to it from the MCP servers dialog.",
                server.Name);
        }

        return access.AccessToken;
    }

    // Mirrors PluginSessionDriverAdapter's mapping: HTTP → url with the credential this server needs (a static API
    // key, or the token from the cockpit's own OAuth sign-in — AC-353), plus a CockpitHosted flag for a cockpit
    // loopback endpoint (auth via the COCKPIT_MCP_KEY env var, no literal here — AC-40); stdio → command/args. A
    // server missing its transport target is dropped.
    private static PluginMcpServer? _ToPluginMcpServer(McpServerConfig server, string? oauthAccessToken) => server.Transport switch
    {
        McpTransport.Http when !string.IsNullOrWhiteSpace(server.Url) => new PluginMcpServer
        {
            Name = server.Name,
            Url = server.Url,
            BearerToken = CockpitMcpBearer.UserCredential(server, oauthAccessToken),
            CockpitHosted = server.CockpitHosted,
        },
        McpTransport.Stdio when !string.IsNullOrWhiteSpace(server.Command) => new PluginMcpServer
        {
            Name = server.Name,
            Command = server.Command,
            Args = server.Args,
        },
        _ => null,
    };

    /// <summary>
    /// A plugin says "resume this conversation, or the last one" and nothing else. The core's <see cref="SessionResume"/>
    /// also has a "start fresh" case, which is the absence of a resume — so it maps to null rather than to an
    /// object that says nothing.
    /// </summary>
    private static PluginTtyResume? _Resume(SessionResume? resume) => resume switch
    {
        { Mode: SessionResumeMode.MostRecent } => new PluginTtyResume(null),
        { Mode: SessionResumeMode.BySessionId, SessionId: { } id } when !string.IsNullOrWhiteSpace(id) => new PluginTtyResume(id.Trim()),
        _ => null,
    };
}
