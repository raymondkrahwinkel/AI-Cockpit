using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Cockpit.App.ViewModels;
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
/// <paramref name="services"/> rather than a constructor-injected <see cref="CockpitViewModel"/>: the view model
/// is what launches sessions, so depending on it directly would close a cycle. Resolved lazily at call time, the
/// same way <see cref="CockpitHost.GetProjectFieldValueAsync"/> reaches it.
/// </remarks>
internal sealed class SessionResourceResolver(
    ISessionResourceProviderRegistry registry,
    IServiceProvider services,
    ILogger<SessionResourceResolver> logger)
    : ISessionResourceResolver, ISingletonService
{
    public async Task<SessionResources> ResolveAsync(string? paneId, CancellationToken cancellationToken = default)
    {
        var providers = registry.Providers;
        if (providers.Count == 0 || string.IsNullOrEmpty(paneId))
        {
            return SessionResources.Empty;
        }

        var request = new SessionResourceRequest(paneId, await _ProjectIdOfAsync(paneId).ConfigureAwait(false));

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

    /// <summary>
    /// One plugin's answer, mapped into host types, with its environment already scrubbed so the warning below can
    /// name the plugin that sent it — <see cref="SessionResourceMerge"/> drops the same keys again, because the
    /// guarantee belongs where the value is used and this pass exists only to attribute it.
    /// </summary>
    private async Task<SessionResources> _AskAsync(
        ISessionResourceProvider provider,
        SessionResourceRequest request,
        CancellationToken cancellationToken)
    {
        SessionResourceContribution contribution;
        try
        {
            contribution = await provider.GetSessionResourcesAsync(request, cancellationToken).ConfigureAwait(false)
                ?? SessionResourceContribution.None;
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

        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        var rejected = new List<string>();
        foreach (var variable in contribution.EnvironmentVariables)
        {
            if (TtyEnvironment.IsHostControlled(variable.Key))
            {
                rejected.Add(variable.Key);
                continue;
            }

            environment[variable.Key] = variable.Value;
        }

        if (rejected.Count > 0)
        {
            logger.LogWarning(
                "Plugin {Provider} tried to set host-controlled environment variables; ignored: {Variables}",
                provider.GetType().Name,
                string.Join(", ", rejected));
        }

        return new SessionResources(environment);
    }

    /// <summary>
    /// Which project the pane's session belongs to, or null when it has none or the pane is not on screen yet. The
    /// lookup walks the on-screen session collections, so it happens on the UI thread; a launch route may ask from
    /// any.
    /// </summary>
    private async Task<string?> _ProjectIdOfAsync(string paneId)
    {
        if (services.GetService<CockpitViewModel>() is not { } cockpit)
        {
            return null;
        }

        return Dispatcher.UIThread.CheckAccess()
            ? cockpit.FindSession(paneId)?.ProjectId
            : await Dispatcher.UIThread.InvokeAsync(() => cockpit.FindSession(paneId)?.ProjectId);
    }
}
