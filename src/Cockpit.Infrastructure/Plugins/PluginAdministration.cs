using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Delegation;
using Cockpit.Core.Plugins;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Infrastructure.Sessions.Tty;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Infrastructure.Plugins;

// AC-1434: the in-proc IPluginAdministration over discovery, the registrations, the installer and the diagnostics. A
// backend that loaded no plugins has no diagnostics, and then reports none.
internal sealed class PluginAdministration(
    PluginBootstrap bootstrap,
    IPluginRegistrationStore registrations,
    IPluginInstaller installer,
    IPluginProvisioningService provisioning,
    IPluginStoreClient storeClient,
    PluginDiagnostics? diagnostics = null,
    PluginManager? manager = null,
    ISessionRegistry? sessions = null,
    IPluginProviderRegistry? providers = null,
    IPluginTtyProviderRegistry? ttyProviders = null,
    IDelegationService? delegation = null,
    ISessionProfileStore? profiles = null) : IPluginAdministration
{
    public async Task<IReadOnlyList<InstalledPlugin>> GetInstalledAsync(CancellationToken cancellationToken = default)
    {
        var discovered = await bootstrap.DiscoverForDisplayAsync(AbstractionsContract.Version, cancellationToken).ConfigureAwait(false);
        var saved = await registrations.LoadAllAsync(cancellationToken).ConfigureAwait(false);

        return discovered
            .Select(plugin => _Installed(plugin, saved.GetValueOrDefault(plugin.FolderId)))
            .OrderBy(plugin => plugin.Registration?.MenuOrder ?? 0)
            .ToList();
    }

    public Task<PluginStoreFetchResult> FetchStoreIndexAsync(PluginStoreConfig store, CancellationToken cancellationToken = default) =>
        storeClient.FetchIndexAsync(store, cancellationToken);

    public async Task<PluginInstallResult> InstallFromZipAsync(string zipFilePath, CancellationToken cancellationToken = default)
    {
        var result = await installer.InstallFromZipAsync(zipFilePath, AbstractionsContract.Version, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.Staged)
        {
            await _RepinAsync(result.FolderId, result.Sha256, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    public async Task<PluginProvisionResult> InstallFromStoreAsync(PluginProvisionRequest request, CancellationToken cancellationToken = default)
    {
        var result = await provisioning.InstallAsync(request, AbstractionsContract.Version, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.Outcome == PluginProvisionOutcome.Staged)
        {
            await _RepinAsync(result.FolderId, result.Sha256, cancellationToken).ConfigureAwait(false);
            return result with { Live = PluginLiveState.Restart("An update is applied when the host restarts.") };
        }

        return result;
    }

    public async Task<PluginLiveState> SetEnabledAsync(string folderId, bool enabled, string pinnedSha256, CancellationToken cancellationToken = default)
    {
        await registrations.SaveAsync(folderId, new PluginRegistration(Enabled: enabled, PinnedSha256: pinnedSha256), cancellationToken).ConfigureAwait(false);
        return enabled
            ? await _ActivateAsync(folderId, cancellationToken).ConfigureAwait(false)
            : await _DeactivateAsync(folderId).ConfigureAwait(false);
    }

    // The registration is saved already, so whatever goes wrong here a restart leaves the plugin out.
    private async Task<PluginLiveState> _DeactivateAsync(string folderId)
    {
        if (manager is null)
        {
            return PluginLiveState.Restart("This host cannot deactivate a plugin while it runs.");
        }

        if (await _DeactivationBlockerAsync(manager, folderId).ConfigureAwait(false) is { } blocker)
        {
            return PluginLiveState.Restart(blocker);
        }

        var result = await manager.DeactivateAsync(folderId).ConfigureAwait(false);
        return result.Status is PluginActivationStatus.Deactivated or PluginActivationStatus.NotActive
            ? PluginLiveState.Active
            : PluginLiveState.Restart(result.Reason ?? "The plugin could not be deactivated.");
    }

    // What a live deactivation would cut off: a flagged plugin, or a session still running on one of its providers.
    private async Task<string?> _DeactivationBlockerAsync(PluginManager loaded, string folderId)
    {
        if (loaded.LoadedParts.Any(entry => entry.Discovered.FolderId == folderId && entry.Discovered.Manifest.RequiresRestart))
        {
            return "The plugin asks for a restart to deactivate.";
        }

        var running = sessions is null ? [] : sessions.All.Concat(sessions.Assistant is { } assistant ? [assistant] : []);
        var user = running.FirstOrDefault(session => session.ProviderId is { } providerId && _Owns(folderId, providerId));
        if (user is not null)
        {
            return $"Session '{user.Title}' uses this provider.";
        }

        // A delegated task has no pane, so it is not in the registry.
        if (delegation is null || profiles is null)
        {
            return null;
        }

        var labels = (await profiles.LoadAsync().ConfigureAwait(false))
            .Where(profile => profile.ProviderConfig is PluginProviderConfig plugin && _Owns(folderId, plugin.ProviderId))
            .Select(profile => profile.Label)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var task = delegation.ListTasks().FirstOrDefault(candidate =>
            candidate.Status is DelegatedTaskStatus.Queued or DelegatedTaskStatus.Running && labels.Contains(candidate.ProfileLabel));
        return task is null ? null : $"Delegated task '{task.Label ?? task.TaskId}' uses this provider.";
    }

    private bool _Owns(string folderId, string providerId) =>
        providers?.OwnerOf(providerId) == folderId || ttyProviders?.OwnerOf(providerId) == folderId;

    // Any failure falls back to a restart, which loads the plugin the ordinary way.
    private async Task<PluginLiveState> _ActivateAsync(string folderId, CancellationToken cancellationToken)
    {
        if (manager is null)
        {
            return PluginLiveState.Restart("This host cannot activate a plugin while it runs.");
        }

        try
        {
            var discovered = (await bootstrap.DiscoverForDisplayAsync(AbstractionsContract.Version, cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(plugin => plugin.FolderId == folderId);
            if (discovered is null)
            {
                return PluginLiveState.Restart("The plugin is no longer on disk.");
            }

            var result = await Task.Run(() => manager.ActivateOne(discovered), cancellationToken).ConfigureAwait(false);
            return result.Status is PluginActivationStatus.Activated or PluginActivationStatus.AlreadyActive
                ? PluginLiveState.Active
                : PluginLiveState.Restart(result.Reason ?? "The plugin could not be activated.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return PluginLiveState.Restart(exception.Message);
        }
    }

    // The marker goes first, so a deactivation that has to wait for a restart still removes the folder on that start.
    public async Task<PluginLiveState> RemoveAsync(string folderId, CancellationToken cancellationToken = default)
    {
        await installer.MarkForRemovalAsync(folderId, cancellationToken).ConfigureAwait(false);
        await registrations.RemoveAsync(folderId, cancellationToken).ConfigureAwait(false);
        return await _DeactivateAsync(folderId).ConfigureAwait(false);
    }

    // Three independent facts can live in one folder's history (#184): whether it ever became operative, whether it is
    // flagged as built for a newer SDK, and whether a contribution it registered later failed.
    private InstalledPlugin _Installed(DiscoveredPlugin plugin, PluginRegistration? registration)
    {
        var failures = diagnostics?.AllForFolder(plugin.FolderId) ?? [];

        return new InstalledPlugin(
            plugin,
            registration,
            failures.LastOrDefault(failure => IPluginDiagnostics.ActivationPhases.Contains(failure.Phase))?.Error,
            failures.LastOrDefault(failure => failure.Phase == "compatibility")?.Error,
            failures.LastOrDefault(failure => failure.Phase == "mcp-server")?.Error);
    }

    // A staged update is live only after the restart: pin the new bytes now, so the swap comes back as enabled as it
    // was. No registration means the operator removed it and installed it again before the restart (AC-455).
    private async Task _RepinAsync(string? folderId, string? sha256, CancellationToken cancellationToken)
    {
        if (folderId is null || sha256 is null)
        {
            return;
        }

        var saved = await registrations.LoadAllAsync(cancellationToken).ConfigureAwait(false);
        if (saved.TryGetValue(folderId, out var prior))
        {
            await registrations.SaveAsync(folderId, new PluginRegistration(Enabled: prior.Enabled, PinnedSha256: sha256), cancellationToken).ConfigureAwait(false);
        }
    }
}
