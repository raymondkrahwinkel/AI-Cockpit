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
    ILogger<PluginTtySessionProviderAdapter>? logger = null,
    ISessionConversationSink? conversationSink = null,
    IWorktreeManager? worktreeManager = null,
    SessionMcpMounts? mcpMounts = null,
    SessionMcpGatewayHost? gateway = null) : ITtySessionProvider
{
    public string ProviderId => providerId;

    public TtyLaunchSpec BuildLaunch(TtyLaunchContext context)
    {
        _gatewayServerNames = null;
        // Registry lookup and permission-rule preparation share one launch budget.
        using var budget = new CancellationTokenSource(RenewalBudget);
        var (mcpServers, canDelegate) = _ResolveRegistry(context.EnabledMcpServerNames, context.ProjectId, context.WorkingDirectory, context.Profile?.Label, context, budget.Token);

        // The launcher owns the pane identity, carried in the base environment.
        var paneId = _PaneId(context.BaseEnvironment);

        // AC-927: what this launch actually hands the TUI, so the header names those servers rather than the
        // checklist it was started from — which never holds the always-mounted and auto-mounted ones.
        if (paneId is { Length: > 0 })
        {
            if (_gatewayServerNames is not null)
            {
                mcpMounts?.ReportConnections(paneId, _gatewayServerNames, [],
                    exception => logger?.LogWarning(exception, "MCP connection-report handler failed."));
            }
        }

        // The base environment is handed straight through: the host (TtyLauncher) has already put this run's MCP
        // auth key on it (COCKPIT_MCP_KEY, AC-40) so a cockpit-hosted server's config can reference the env var
        // rather than embed a literal — it is not the adapter's to add, only to relay.
        var warmedNames = false;
        IReadOnlyList<string> TranslateRule(string rule)
        {
            if (PluginMcpServerMapper.GatewayToolNames(mcpServers, paneId, mcpMounts) is not { } names)
            {
                return [rule];
            }
            if (!warmedNames && (rule.Contains('*') || !rule[5..].Contains("__", StringComparison.Ordinal)) && paneId is not null && gateway is not null)
            {
                Task.Run(() => gateway.PrepareLaunchAsync(paneId, budget.Token), budget.Token).GetAwaiter().GetResult();
                warmedNames = true;
            }
            return names.TranslateRule(rule);
        }
        PluginTtyLaunchSpec spec;
        try
        {
            spec = Build();
        }
        catch (Exception exception)
        {
            logger?.LogError(exception, "Preparing the MCP launch configuration failed; session launch was stopped.");
            throw new InvalidOperationException("Session could not start because its MCP launch configuration could not be prepared. See the Cockpit log for the cause.");
        }
        PluginTtyLaunchSpec Build() => inner.BuildLaunch(new PluginTtyLaunchContext(
            configJson,
            context.Options,
            context.WorkingDirectory,
            _Resume(context.Resume),
            context.BaseEnvironment)
        {
            McpServers = mcpServers,
            TranslateMcpPermissionRule = TranslateRule,
            DelegationSystemPrompt = canDelegate ? DelegationSystemPrompt.Default : null,
            // No sink or no pane id means nowhere to report to, so the provider gets no callback at all — a
            // provider that always null-checks before calling it (the documented contract) stays correct either way.
            ReportConversationId = conversationSink is null || paneId is not { Length: > 0 }
                ? null
                : conversation => conversationSink.Report(paneId, conversation.ToCore()),
        });

        if (_gatewayServerNames is not null && paneId is not null)
        {
            gateway?.SetToolAuthorization(paneId, (name, input) => inner is IPluginMcpToolAuthorization policy && policy.MayCallWithoutPrompt(name, input));
        }
        return new TtyLaunchSpec(
            spec.ExecutablePath,
            spec.Arguments,
            spec.EnvironmentOverlay,
            spec.WorkingDirectory,
            spec.SessionScopedFiles,
            spec.StatusFile) { Model = spec.Model };
    }

    private (IReadOnlyList<PluginMcpServer> McpServers, bool CanDelegate) _ResolveRegistry(IReadOnlySet<string>? enabledServerNames, string? projectId, string? workingDirectory, string? agentName, TtyLaunchContext context, CancellationToken budget)
    {
        try
        {
            var paneId = _PaneId(context.BaseEnvironment) ?? throw new InvalidOperationException("The MCP launch has no pane.");
            var url = gateway?.UrlFor(paneId) ?? throw new InvalidOperationException("The session MCP gateway is unavailable.");
            if (mcpServerCatalog is null || mcpMounts is null)
            {
                throw new InvalidOperationException("The session MCP services are unavailable.");
            }
            if (mcpMounts.Set(paneId) is { PreserveSelection: true } desired)
            {
                enabledServerNames = new ExactMcpServerSelection(desired.ServerNames) { AssistantManaged = desired.Context?.Manager == SessionMcpManager.Assistant };
            }
            var registry = mcpServerCatalog.GetServersForSessionAsync(projectId, enabledServerNames, budget).GetAwaiter().GetResult();
            var autoMounted = GitHubPullRequestsAutoMount.NamesAsync(worktreeManager, workingDirectory, budget).GetAwaiter().GetResult();
            var effectiveSelection = McpServerRegistryFilter.WithAutoMountedServers(enabledServerNames, registry, autoMounted);
            var eligible = McpServerRegistryFilter.ApplySessionSelection(registry, effectiveSelection).Where(McpConfigFile.IsAgentEligible).ToList();
            var confineRoot = context.Options.GetValueOrDefault(WellKnownPluginSessionOptions.ConfineFileToolsToWorkingDirectory) == "true" ? workingDirectory : null;
            Task.Run(() => gateway.BindLaunchAsync(paneId, projectId, workingDirectory, confineRoot, agentName, enabledServerNames is ExactMcpServerSelection { AssistantManaged: true }, budget, context.BaseEnvironment.ToDictionary(pair => pair.Key, pair => (string?)pair.Value), context.Options.GetValueOrDefault(WellKnownPluginSessionOptions.Brain)), budget).GetAwaiter().GetResult();
            var names = eligible.Select(server => server.Name).ToList();
            mcpMounts.Grant(paneId, names);
            gateway.PrepareLaunchAsync(paneId, budget, waitForNames: false).GetAwaiter().GetResult();
            _gatewayServerNames = names;
            return ([new PluginMcpServer { Name = "cockpit", Url = url, CockpitHosted = true }],
                eligible.Any(server => string.Equals(server.Name, DelegationMcp.ServerName, StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception exception)
        {
            logger?.LogError(exception, "Preparing the session MCP gateway failed; session launch was stopped.");
            throw new InvalidOperationException("Session could not start because its MCP gateway is unavailable. See the Cockpit log for the cause.");
        }
    }

    private IReadOnlyList<string>? _gatewayServerNames;
    private static readonly TimeSpan RenewalBudget = TimeSpan.FromSeconds(5);

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
