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

// Runs a plugin's `IPluginTtyProvider` as one of the cockpit's own `ITtySessionProvider`s.
// The two contracts say the same thing in two vocabularies — the plugin SDK cannot reference the core's types
// without binding every plugin to the core's version of them — so this is where one becomes the other.
// The host resolves the things a plugin cannot reach across the isolation boundary and hands them through the
// grown context (Fase 4): the shared MCP registry (#26) and whether the orchestrator is enabled (#67), so a rich
// TUI like Claude can fan the registry into `--mcp-config` and append the delegation prompt. The status
// snapshot file the provider names in its spec is carried back to the core spec, so the session header still
// polls the provider's limits.
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

    // The agent-eligible MCP servers and whether the orchestrator is enabled — read once per launch. The
    // per-session selection (#44) narrows the eligible registry to the operator's checklist first (an unchecked
    // server never reaches the CLI), exactly as the SDK route's `PluginSessionDriverAdapter` does; a
    // `null` selection means no narrowing. Delegation is judged on the *selected* set: if
    // the operator unchecked the orchestrator server, this session cannot delegate. Sync (the spawn path is
    // synchronous) and best-effort: no store (a unit test wiring none) or a read failure means no servers and no
    // delegation, rather than blocking the launch. `projectId` (AC-218) scopes the registry read
    // to that project's own view — resolved host-side into the launch context, never a paneId→projectId lookup
    // here (that would need a UI-thread hop this synchronous spawn path cannot take).
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
                    // Same rule and the same reasoning as the SDK route (see PluginSessionDriverAdapter): handing it
                    // over anyway only moves the refusal into the CLI's own initialize, where it reads as a broken
                    // server rather than an absent one. The operator gets the coordinator's line naming the cause and
                    // the action instead.
                    continue;
                }

                if (_ToPluginMcpServer(server, access.AccessToken, proxyUrl) is { } mapped)
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

    // How long a launch will wait, in total, for stale tokens to be renewed. This path is synchronous all the way out
    // to `ITtyLauncher.Launch`, which is reached from the UI thread, so the wait is the window in which the app
    // stops repainting. A renewal is one token-endpoint round trip and either answers well within this or is not
    // going to; letting it run unbounded would trade a session's missing tools for a frozen application.
    private static readonly TimeSpan RenewalBudget = TimeSpan.FromSeconds(5);

    // The credential this launch presents to `server` (AC-353). Asked for non-interactively, so a
    // launch never makes a browser window appear on its own; a server whose authorization cannot be renewed is left
    // out of the launch and said so, rather than the CLI meeting a 401 later with no way to act on it.
    //
    // Blocking, like the registry read a few lines up and for the same reason: this spawn path is synchronous. That
    // read is local and quick; this one can go to the network when a stored token has expired, which is ordinary
    // rather than rare — hence the budget above, and never an unbounded wait.
    //
    // `theSessionWillHoldIt`:
    // Whether the credential is written into the launch's config and kept there — see the SDK route's own
    // parameter of the same name. Behind the loopback endpoint it is not, and the question narrows to whether a
    // sign-in exists at all.
    private McpOAuthAccess _AcquireCredential(McpServerConfig server, bool theSessionWillHoldIt, CancellationToken budget)
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

            // Carries the cause rather than returning here, so the line below says something the operator can act
            // on: a renewal that ran out of the launch's time is the server not answering in time, and the advice
            // for that is to wait for it rather than to sign in again. Returning early left that answer blank, and
            // blank falls through to the sentence for "no reason given".
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
    // the credential above and blocking for the same reason — this spawn path is synchronous out to the launcher.
    // Binding a loopback listener is local and quick, but it shares the budget rather than getting one of its own:
    // the budget is the window in which the application stops repainting, and it is the launch's in total.
    //
    // Anything that goes wrong answers `null`, which falls back to writing the token into the
    // config — degraded, and no worse than before this existed.
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

    // Mirrors PluginSessionDriverAdapter's mapping: an OAuth server the cockpit proxies is addressed at that
    // loopback endpoint with no literal token (AC-524); otherwise HTTP → url with the credential this server needs
    // (a static API key, or the token from the cockpit's own OAuth sign-in — AC-353), plus a CockpitHosted flag for
    // a cockpit loopback endpoint (auth via the COCKPIT_MCP_KEY env var, no literal here — AC-40); stdio →
    // command/args. A server missing its transport target is dropped.
    private static PluginMcpServer? _ToPluginMcpServer(McpServerConfig server, string? oauthAccessToken, string? oauthProxyUrl) => server.Transport switch
    {
        McpTransport.Http when oauthProxyUrl is { Length: > 0 } => new PluginMcpServer
        {
            Name = server.Name,
            Url = oauthProxyUrl,
            Headers = McpAgentHeaders.For(server, null),
            CockpitHosted = true,
        },
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
