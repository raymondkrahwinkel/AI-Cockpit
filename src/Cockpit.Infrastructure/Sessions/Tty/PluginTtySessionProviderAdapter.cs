using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Delegation;
using Cockpit.Core.Mcp;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Sessions;
using Cockpit.Core.Sessions.Permissions;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Plugins.Abstractions.Sessions;

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
    McpAuthKey authKey,
    IMcpServerCatalog? mcpServerCatalog = null) : ITtySessionProvider
{
    public string ProviderId => providerId;

    public TtyLaunchSpec BuildLaunch(TtyLaunchContext context)
    {
        var (mcpServers, canDelegate) = _ResolveRegistry();

        // This run's MCP auth key rides the base environment (AC-40), so a cockpit-hosted server's config can
        // reference COCKPIT_MCP_KEY rather than embed a literal. It is not host-controlled, so the pty base scrub
        // passes it through to the child.
        var baseEnvironment = context.BaseEnvironment is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(context.BaseEnvironment, StringComparer.Ordinal);
        baseEnvironment[WellKnownSessionEnvironment.CockpitMcpKey] = authKey.Value;

        var spec = inner.BuildLaunch(new PluginTtyLaunchContext(
            configJson,
            context.Options,
            context.WorkingDirectory,
            _Resume(context.Resume),
            baseEnvironment)
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
    /// The agent-eligible MCP servers and whether the orchestrator is enabled — read once per launch. The TTY route
    /// fans the whole eligible registry (no per-session narrowing, matching the in-tree Claude provider). Sync
    /// (the spawn path is synchronous) and best-effort: no store (a unit test wiring none) or a read failure means
    /// no servers and no delegation, rather than blocking the launch.
    /// </summary>
    private (IReadOnlyList<PluginMcpServer> McpServers, bool CanDelegate) _ResolveRegistry()
    {
        if (mcpServerCatalog is null)
        {
            return ([], false);
        }

        try
        {
            var registry = mcpServerCatalog.GetServersAsync().GetAwaiter().GetResult();
            var servers = registry
                .Where(McpConfigFile.IsAgentEligible)
                .Select(_ToPluginMcpServer)
                .OfType<PluginMcpServer>()
                .ToList();
            var canDelegate = registry.Any(server =>
                server.Enabled && string.Equals(server.Name, DelegationMcp.ServerName, StringComparison.OrdinalIgnoreCase));
            return (servers, canDelegate);
        }
        catch (Exception)
        {
            return ([], false);
        }
    }

    // Mirrors PluginSessionDriverAdapter's mapping: HTTP → url with the user API-key server's own bearer, plus a
    // CockpitHosted flag for a cockpit loopback endpoint (auth via the COCKPIT_MCP_KEY env var, no literal here —
    // AC-40); stdio → command/args. A server missing its transport target is dropped.
    private static PluginMcpServer? _ToPluginMcpServer(McpServerConfig server) => server.Transport switch
    {
        McpTransport.Http when !string.IsNullOrWhiteSpace(server.Url) => new PluginMcpServer
        {
            Name = server.Name,
            Url = server.Url,
            BearerToken = CockpitMcpBearer.UserApiKey(server),
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
