using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Delegation;
using Cockpit.Core.Mcp;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Sessions;
using Cockpit.Core.Sessions.Permissions;
using Cockpit.Core.Sessions.Tty;
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
    ILogger<PluginTtySessionProviderAdapter>? logger = null,
    ISessionConversationSink? conversationSink = null) : ITtySessionProvider
{
    public string ProviderId => providerId;

    public TtyLaunchSpec BuildLaunch(TtyLaunchContext context)
    {
        var (mcpServers, canDelegate) = _ResolveRegistry(context.EnabledMcpServerNames, context.ProjectId);

        // AC-408: the same pane id TtyLauncher puts on the base environment as COCKPIT_PANE_ID, read back here
        // (rather than added to TtyLaunchContext itself) so ReportConversationId below knows which pane a later
        // report belongs to. Null when the launch carries no pane id (a profile-less quick session).
        var paneId = _PaneId(context.BaseEnvironment);

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
            // No sink or no pane id means nowhere to report to, so the provider gets no callback at all — a
            // provider that always null-checks before calling it (the documented contract) stays correct either way.
            ReportConversationId = conversationSink is null || paneId is not { Length: > 0 }
                ? null
                : conversation => conversationSink.Report(paneId, conversation.ToCore()),
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
            var servers = new List<PluginMcpServer>();

            // One budget for the whole launch, not one per server: this is the window in which the application stops
            // repainting, and four stale servers against an unreachable host would otherwise add up to four times the
            // wait the budget promises.
            using var budget = new CancellationTokenSource(RenewalBudget);

            foreach (var server in selected.Where(McpConfigFile.IsAgentEligible))
            {
                var access = _AcquireCredential(server, budget.Token);
                if (access.State == McpAuthState.AuthorizationRequired)
                {
                    // Same rule as the SDK route: a server the agent cannot authenticate to is left out rather than
                    // handed over bare, so the refusal is something the operator is told here instead of something
                    // the agent meets later with nothing to act on.
                    continue;
                }

                if (_ToPluginMcpServer(server, access.AccessToken) is { } mapped)
                {
                    servers.Add(mapped);
                }
            }
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
    /// How long a launch will wait, in total, for stale tokens to be renewed. This path is synchronous all the way out
    /// to <c>ITtyLauncher.Launch</c>, which is reached from the UI thread, so the wait is the window in which the app
    /// stops repainting. A renewal is one token-endpoint round trip and either answers well within this or is not
    /// going to; letting it run unbounded would trade a session's missing tools for a frozen application.
    /// </summary>
    private static readonly TimeSpan RenewalBudget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The credential this launch presents to <paramref name="server"/> (AC-353). Asked for non-interactively, so a
    /// launch never makes a browser window appear on its own; a server whose authorization cannot be renewed is left
    /// out of the launch and said so, rather than the CLI meeting a 401 later with no way to act on it.
    /// <para>
    /// Blocking, like the registry read a few lines up and for the same reason: this spawn path is synchronous. That
    /// read is local and quick; this one can go to the network when a stored token has expired, which is ordinary
    /// rather than rare — hence the budget above, and never an unbounded wait.
    /// </para>
    /// </summary>
    private McpOAuthAccess _AcquireCredential(McpServerConfig server, CancellationToken budget)
    {
        if (oauthCoordinator is null || server.Auth != McpServerAuth.OAuth)
        {
            return McpOAuthAccess.NotRequired;
        }

        McpOAuthAccess access;
        try
        {
            // Run on the pool rather than awaiting inline: a continuation that captured this thread's context could
            // not resume while GetResult() is holding it, and a budget cannot lift a deadlock — the operation would
            // finish and the continuation would still be waiting for the thread that is waiting for it. Cockpit's own
            // chain configures away from the context throughout, but the MCP client's does not answer to us.
            access = Task.Run(() => oauthCoordinator.AcquireAsync(server, interactive: false, budget), budget)
                .GetAwaiter()
                .GetResult();
        }
        catch (OperationCanceledException)
        {
            logger?.LogWarning(
                "Renewing the authorization for MCP server {Name} took longer than the launch waits for, so this session starts without it.",
                server.Name);
            return McpOAuthAccess.AuthorizationRequired;
        }

        if (access.State == McpAuthState.AuthorizationRequired)
        {
            logger?.LogWarning(
                "MCP server {Name} has no sign-in the cockpit can use, so this session starts without it. Connect to it from a session that uses the cockpit's own tools to sign in.",
                server.Name);
        }

        return access;
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
            Headers = McpAgentHeaders.For(server, CockpitMcpBearer.UserCredential(server, oauthAccessToken)),
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

    // COCKPIT_PANE_ID rides the base environment rather than TtyLaunchContext.Options (AC-13 — TtyLauncher sets it
    // there for the spawned process to read, not for a provider adapter), so it is read back the same way here.
    private static string? _PaneId(IReadOnlyDictionary<string, string> baseEnvironment)
    {
        foreach (var (key, value) in baseEnvironment)
        {
            if (TtyEnvironment.IsCockpitPaneIdMarker(key))
            {
                return value;
            }
        }

        return null;
    }
}
