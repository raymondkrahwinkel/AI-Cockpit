using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Worktrees;
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

// Runs a plugin's `IPluginTtyProvider` as one of the cockpit's own `ITtySessionProvider`s — the plugin SDK cannot
// reference the core's types, so this adapts between them. It also resolves what the plugin cannot reach across the
// isolation boundary (the MCP registry, delegation eligibility) and carries the provider's status file back to the core spec.
internal sealed class PluginTtySessionProviderAdapter(
    string providerId,
    IPluginTtyProvider inner,
    string configJson,
    IMcpServerCatalog? mcpServerCatalog = null,
    IMcpOAuthCoordinator? oauthCoordinator = null,
    ILogger<PluginTtySessionProviderAdapter>? logger = null,
    ISessionConversationSink? conversationSink = null,
    IMcpOAuthProxy? oauthProxy = null,
    IWorktreeManager? worktreeManager = null,
    SessionMcpMounts? mcpMounts = null) : ITtySessionProvider
{
    public string ProviderId => providerId;

    public TtyLaunchSpec BuildLaunch(TtyLaunchContext context)
    {
        var (mcpServers, canDelegate) = _ResolveRegistry(context.EnabledMcpServerNames, context.ProjectId, context.WorkingDirectory);

        // AC-408: the same pane id TtyLauncher puts on the base environment as COCKPIT_PANE_ID, read back here
        // (rather than added to TtyLaunchContext itself) so ReportConversationId below knows which pane a later
        // report belongs to. Null when the launch carries no pane id (a profile-less quick session).
        var paneId = _PaneId(context.BaseEnvironment);

        // AC-927: what this launch actually hands the TUI, so the header names those servers rather than the
        // checklist it was started from — which never holds the always-mounted and auto-mounted ones.
        if (paneId is { Length: > 0 })
        {
            mcpMounts?.Report(paneId, [.. mcpServers.Select(server => server.Name)]);
        }

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

    // AC-1013: Agent-eligible MCP servers and delegation eligibility, resolved once per launch off the operator's
    // checklist (AC-218 scopes to the project, resolved host-side — never a paneId→projectId lookup here, which
    // would need a UI-thread hop this sync spawn path can't take); best-effort — a missing store/read failure yields no servers/delegation, never blocks the launch.
    private (IReadOnlyList<PluginMcpServer> McpServers, bool CanDelegate) _ResolveRegistry(IReadOnlySet<string>? enabledServerNames, string? projectId, string? workingDirectory)
    {
        if (mcpServerCatalog is null)
        {
            return ([], false);
        }

        try
        {
            var registry = mcpServerCatalog.GetServersForProjectAsync(projectId).GetAwaiter().GetResult();
            // AC-869: cockpit-github-pull-requests is Internal (hidden from every picker); a git-repo working
            // directory names it explicitly here rather than through operator config. Blocking is consistent with
            // the catalog read above — this spawn path is synchronous all the way out to ITtyLauncher.Launch.
            var autoMounted = GitHubPullRequestsAutoMount.NamesAsync(worktreeManager, workingDirectory, CancellationToken.None).GetAwaiter().GetResult();
            var effectiveSelection = McpServerRegistryFilter.WithAutoMountedServers(enabledServerNames, registry, autoMounted);
            var selected = McpServerRegistryFilter.ApplySessionSelection(registry, effectiveSelection);
            var servers = new List<PluginMcpServer>();

            // One budget for the whole launch, not one per server: this is the window in which the application stops
            // repainting, and four stale servers against an unreachable host would otherwise add up to four times the
            // wait the budget promises.
            using var budget = new CancellationTokenSource(RenewalBudget);

            foreach (var server in selected.Where(McpConfigFile.IsAgentEligible))
            {
                // The endpoint first, for the same reason as on the SDK route: behind it the launch never writes a
                // token, so it only has to establish that a sign-in exists; without it the token goes into the
                // config and has to outlast the sitting.
                var proxyUrl = _ProxyUrl(server, budget.Token);
                var access = _AcquireCredential(server, theSessionWillHoldIt: proxyUrl is null, budget.Token);
                if (access.State == McpAuthState.AuthorizationRequired)
                {
                    // Same reasoning as the SDK route's PluginSessionDriverAdapter: handing it over anyway would only
                    // move the refusal into the CLI's own initialize, reading as a broken server rather than an
                    // absent one — the operator gets the coordinator's line naming the cause and action instead.
                    continue;
                }

                if (PluginMcpServerMapper.ToPluginMcpServer(server, access.AccessToken, proxyUrl) is { } mapped)
                {
                    servers.Add(mapped);
                }
                else
                {
                    logger?.LogWarning(
                        "MCP server {Name} is agent-eligible but not mountable (no url for Http / no command for Stdio); it was skipped.",
                        server.Name);
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

    // How long a launch waits, in total, for stale tokens to be renewed. This path runs synchronously out to
    // `ITtyLauncher.Launch` on the UI thread, so the wait is the window in which the app stops repainting — a
    // renewal answers well within this or not at all, and running unbounded would trade missing tools for a frozen app.
    private static readonly TimeSpan RenewalBudget = TimeSpan.FromSeconds(5);

    // AC-1013: The credential this launch presents to `server` (AC-353), asked for non-interactively; a server
    // whose authorization cannot be renewed within the shared launch budget is left out rather than surfacing as a
    // 401 later. `theSessionWillHoldIt` mirrors the SDK route's same-named parameter — false behind the loopback proxy.
    private McpOAuthAccess _AcquireCredential(McpServerConfig server, bool theSessionWillHoldIt, CancellationToken budget)
    {
        if (oauthCoordinator is null || server.Auth != McpServerAuth.OAuth)
        {
            return McpOAuthAccess.NotRequired;
        }

        McpOAuthAccess access;
        try
        {
            // Run on the pool rather than awaited inline: a continuation that captured this thread's context could
            // not resume while GetResult() holds it, and a budget cannot lift that deadlock — Cockpit's own chain
            // configures away from context throughout, but the MCP client's does not answer to us.
            access = Task.Run(
                    () => theSessionWillHoldIt
                        ? oauthCoordinator.AcquireForSessionAsync(server, budget)
                        : oauthCoordinator.AcquireAsync(server, interactive: false, budget),
                    budget)
                .GetAwaiter()
                .GetResult();
        }
        catch (OperationCanceledException)
        {
            logger?.LogWarning(
                "Renewing the authorization for MCP server {Name} took longer than the launch waits for.",
                server.Name);

            // Carries the cause rather than returning here, so the line below can act on it: a renewal that ran out
            // of time means the server isn't answering, and the advice is to wait rather than sign in again —
            // returning early would leave that blank, falling through to the generic "no reason given" message.
            access = McpOAuthAccess.AuthorizationRequired with { Reason = McpOAuthAttentionReason.ServerUnreachable };
        }

        if (access.State == McpAuthState.AuthorizationRequired)
        {
            // Information for the same reason as on the SDK route: the coordinator already said it once, at the
            // moment it became true, and a launch is not a new fact.
            logger?.LogInformation(
                "This session starts without MCP server {Name}: {Guidance}",
                server.Name,
                McpOAuthSignInGuidance.For(server.Name, access.Reason));
        }

        return access;
    }

    // The loopback address that stands in for an OAuth server (AC-524), resolved inside the same launch budget as
    // the credential above (this spawn path is synchronous) rather than its own — the budget is the launch's total
    // repaint-freeze window. Anything that fails answers `null`, falling back to writing the token into the config.
    private string? _ProxyUrl(McpServerConfig server, CancellationToken budget)
    {
        if (oauthProxy is null)
        {
            return null;
        }

        try
        {
            return Task.Run(() => oauthProxy.MountAsync(server, budget), budget).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            logger?.LogWarning(
                "Opening the loopback endpoint for MCP server {Name} took longer than the launch waits for; its access token is written into the session config instead.",
                server.Name);
            return null;
        }
    }

    // A plugin says "resume this conversation, or the last one" and nothing else. The core's `SessionResume`
    // also has a "start fresh" case, which is the absence of a resume — so it maps to null rather than to an
    // object that says nothing.
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
