using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Mcp;
using Cockpit.Core.Sessions;
using Cockpit.Core.Sessions.Permissions;
using Cockpit.Core.Sessions.Tty;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Plugins;

// AC-165: asks every registered plugin what it gives a starting session and merges the answers.
// Project lookup for a pane is delegated to `ISessionProjectResolver` (AC-320) rather than
// duplicated here, so every caller shares the one answer.
internal sealed class SessionResourceResolver(
    ISessionResourceProviderRegistry registry,
    ISessionProjectResolver projects,
    ILogger<SessionResourceResolver> logger,
    IMcpServerCatalog? mcpServerCatalog = null)
    : ISessionResourceResolver, ISingletonService
{
    // Long enough that a plugin reading what it already holds is never cut off, short enough that a hung one costs
    // the operator a noticeable pause rather than a session that never opens.
    private static readonly TimeSpan AskTimeout = TimeSpan.FromSeconds(5);

    public async Task<SessionResources> ResolveAsync(string? paneId, IReadOnlySet<string>? enabledMcpServerNames = null, CancellationToken cancellationToken = default)
    {
        var providers = registry.Providers;
        if (providers.Count == 0 || string.IsNullOrEmpty(paneId))
        {
            return SessionResources.Empty;
        }

        var projectId = await projects.ProjectIdOfAsync(paneId, cancellationToken).ConfigureAwait(false);
        var request = new SessionResourceRequest(paneId, projectId)
        {
            McpServers = await _MountedServersAsync(projectId, enabledMcpServerNames, cancellationToken).ConfigureAwait(false),
        };

        var contributions = new List<SessionResources>(providers.Count);
        foreach (var provider in providers)
        {
            if (await _AskAsync(provider, request, cancellationToken).ConfigureAwait(false) is { IsEmpty: false } contribution)
            {
                contributions.Add(contribution);
            }
        }

        if (contributions.Count == 0)
        {
            return SessionResources.Empty;
        }

        var (resources, rejected) = SessionResourceMerge.Merge(contributions);
        if (rejected.Count > 0)
        {
            // Names are safe to log; the values are what a rejected key was trying to smuggle in, so those never are.
            logger.LogWarning(
                "A plugin contributed host-controlled environment variables to session {PaneId}; ignored: {Variables}",
                paneId,
                string.Join(", ", rejected));
        }

        return resources;
    }

    // The servers this launch will mount, narrowed the way both routes narrow them, so a plugin sees what the session
    // gets. Null when there is no catalog or it cannot be read: unknown, never an empty "none".
    private async Task<IReadOnlyList<SessionResourceMcpServer>?> _MountedServersAsync(
        string? projectId,
        IReadOnlySet<string>? enabledMcpServerNames,
        CancellationToken cancellationToken)
    {
        if (mcpServerCatalog is null)
        {
            return null;
        }

        try
        {
            var registry = await mcpServerCatalog.GetServersForProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
            return
            [
                .. McpServerRegistryFilter.ApplySessionSelection(registry, enabledMcpServerNames)
                    .Where(McpConfigFile.IsAgentEligible)
                    .Select(server => new SessionResourceMcpServer(server.Name, server.Command, server.Args)),
            ];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not read the MCP registry to tell plugins which servers the session mounts.");
            return null;
        }
    }

    // One plugin's answer. Host-controlled keys are reported here but not removed here: dropping them is the merge's
    // job, and doing it twice would be the same rule in two places. What this pass adds is the plugin's name, which
    // the merge no longer knows by the time it sees the keys.
    private async Task<SessionResources> _AskAsync(
        ISessionResourceProvider provider,
        SessionResourceRequest request,
        CancellationToken cancellationToken)
    {
        SessionResourceContribution contribution;
        try
        {
            var answer = provider.GetSessionResourcesAsync(request, cancellationToken);

            // Bounded by racing a timer rather than by handing the plugin a deadline it has to honour: a plugin that
            // hangs is exactly the one that will not be observing its cancellation token, and the operator is waiting
            // for a session to open. The abandoned call is left to finish into nothing.
            if (await Task.WhenAny(answer, Task.Delay(AskTimeout, cancellationToken)).ConfigureAwait(false) != answer)
            {
                logger.LogWarning(
                    "Plugin {Provider} did not answer within {Seconds}s; starting the session without its contribution.",
                    provider.GetType().Name,
                    AskTimeout.TotalSeconds);
                return SessionResources.Empty;
            }

            contribution = await answer.ConfigureAwait(false) ?? SessionResourceContribution.None;
        }
        catch (Exception exception)
        {
            // One plugin's bad day must not stop a session opening: its contribution is absent and the failure is
            // logged, the same bargain the MCP catalog strikes when a plugin fails to list its servers.
            logger.LogWarning(
                exception,
                "Plugin {Provider} failed to contribute session resources; starting without them.",
                provider.GetType().Name);
            return SessionResources.Empty;
        }

        var rejected = contribution.EnvironmentVariables.Keys.Where(TtyEnvironment.IsHostControlled).ToList();
        if (rejected.Count > 0)
        {
            logger.LogWarning(
                "Plugin {Provider} tried to set host-controlled environment variables; ignored: {Variables}",
                provider.GetType().Name,
                string.Join(", ", rejected));
        }

        return new SessionResources(contribution.EnvironmentVariables);
    }
}
