using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Sessions;
using Cockpit.Core.Sessions.Tty;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.App.Plugins;

/// <summary>
/// Asks every registered plugin what it gives a starting session and merges the answers (AC-165). The one place
/// that knows both the plugin set and which project a pane belongs to, which is why the launch routes take the
/// interface and this lives here.
/// </summary>
/// <remarks>
/// Which project a pane belongs to comes from <see cref="ISessionProjectResolver"/> rather than being looked up
/// here, so the one answer to that question serves every caller (AC-320).
/// </remarks>
internal sealed class SessionResourceResolver(
    ISessionResourceProviderRegistry registry,
    ISessionProjectResolver projects,
    ILogger<SessionResourceResolver> logger)
    : ISessionResourceResolver, ISingletonService
{
    // Long enough that a plugin reading what it already holds is never cut off, short enough that a hung one costs
    // the operator a noticeable pause rather than a session that never opens.
    private static readonly TimeSpan AskTimeout = TimeSpan.FromSeconds(5);

    public async Task<SessionResources> ResolveAsync(string? paneId, CancellationToken cancellationToken = default)
    {
        var providers = registry.Providers;
        if (providers.Count == 0 || string.IsNullOrEmpty(paneId))
        {
            return SessionResources.Empty;
        }

        var request = new SessionResourceRequest(paneId, await projects.ProjectIdOfAsync(paneId, cancellationToken).ConfigureAwait(false));

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
