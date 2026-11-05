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
    ISessionProfileStore? profiles = null,
    ITrackerProviderRegistry? trackers = null,
    Func<DiscoveredPlugin, TimeSpan>? startGrace = null) : IPluginAdministration
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

    public Task<int> GetRetiredLoadContextsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(manager?.RetiredLoadContexts ?? 0);

    public Task<PluginStoreFetchResult> FetchStoreIndexAsync(PluginStoreConfig store, CancellationToken cancellationToken = default) =>
        storeClient.FetchIndexAsync(store, cancellationToken);

    // AC-2025: an install, update, enable, disable or removal runs alone. An update spans a deactivation and an
    // activation, and a disable or a second update in between would find the plugin gone and put it back.
    // ponytail: one gate for every plugin, so a download holds up a disable; per folder if that is ever felt.
    private readonly SemaphoreSlim _changes = new(1, 1);

    public async Task<PluginInstallResult> InstallFromZipAsync(string zipFilePath, CancellationToken cancellationToken = default)
    {
        await _changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await bootstrap.ReconcileUpdatesAsync(cancellationToken).ConfigureAwait(false);
            var result = await installer.InstallFromZipAsync(zipFilePath, AbstractionsContract.Version, cancellationToken: cancellationToken).ConfigureAwait(false);
            return result.Staged ? result with { Live = await _UpdateAsync(result).ConfigureAwait(false) } : result;
        }
        finally
        {
            _changes.Release();
        }
    }

    public async Task<PluginProvisionResult> InstallFromStoreAsync(PluginProvisionRequest request, CancellationToken cancellationToken = default, IProgress<PluginDownloadProgress>? progress = null)
    {
        await _changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await bootstrap.ReconcileUpdatesAsync(cancellationToken).ConfigureAwait(false);
            var result = await provisioning.InstallAsync(request, AbstractionsContract.Version, cancellationToken: cancellationToken, progress: progress).ConfigureAwait(false);
            return result.Outcome == PluginProvisionOutcome.Staged
                ? result with { Live = await _UpdateAsync(result.FolderId, result.Sha256, result.InPlace, result.ConsentReason, result.ReplacedSha256).ConfigureAwait(false) ?? PluginLiveState.Restart("An update is applied when the host restarts.") }
                : result;
        }
        finally
        {
            _changes.Release();
        }
    }

    public async Task<PluginLiveState> SetEnabledAsync(string folderId, bool enabled, string pinnedSha256, CancellationToken cancellationToken = default)
    {
        await _changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await registrations.SaveAsync(folderId, new PluginRegistration(Enabled: enabled, PinnedSha256: pinnedSha256), cancellationToken).ConfigureAwait(false);
            return enabled
                ? await _ActivateAsync(folderId, cancellationToken: cancellationToken).ConfigureAwait(false)
                : await _DeactivateAsync(folderId).ConfigureAwait(false);
        }
        finally
        {
            _changes.Release();
        }
    }

    // AC-2025: the new bytes are installed. The pin moves to them first (no registration means the operator removed the
    // plugin and installed it again before the restart, AC-455), then the running version gives way to them. Nothing
    // here takes the caller's token: the bytes are replaced already, and a half-done swap would leave no version running.
    private async Task<PluginLiveState> _UpdateAsync(PluginInstallResult installed) =>
        await _UpdateAsync(installed.FolderId, installed.Sha256, installed.InPlace, installed.ConsentReason, installed.ReplacedSha256).ConfigureAwait(false)
        ?? PluginLiveState.Restart("An update is applied when the host restarts.");

    // Null: nothing to swap live, the restart applies it.
    private async Task<PluginLiveState?> _UpdateAsync(string? folderId, string? sha256, bool inPlace, string? consentReason, string? replacedSha256)
    {
        // More capabilities than the approved version had: the bytes wait beside it, unpinned, for the restart's consent step.
        if (consentReason is not null)
        {
            return PluginLiveState.Restart(consentReason);
        }

        var prior = folderId is null ? null : (await registrations.LoadAllAsync(CancellationToken.None).ConfigureAwait(false)).GetValueOrDefault(folderId);
        if (folderId is null || sha256 is null || prior is null)
        {
            return null;
        }

        // The update replaced files that were never approved (an earlier update still waits for approval): approving this
        // one is not the operator's decision to take for them, so it stays unpinned and off until they approve it.
        if (replacedSha256 is not null && !string.Equals(replacedSha256, prior.PinnedSha256, StringComparison.OrdinalIgnoreCase))
        {
            return PluginLiveState.Restart("The files this update replaced were not the approved version. Approve it in Plugin Manager, or the plugin stays off.");
        }

        await registrations.SaveAsync(folderId, new PluginRegistration(Enabled: prior.Enabled, PinnedSha256: sha256), CancellationToken.None).ConfigureAwait(false);
        if (!inPlace || manager is null)
        {
            return null;
        }

        var running = manager.LoadedParts.FirstOrDefault(entry => entry.Discovered.FolderId == folderId).Discovered;
        if (running is null)
        {
            // Nothing runs on the old bytes: an enabled plugin loads on the new ones, a disabled one only holds them.
            return prior.Enabled ? await _ActivateAsync(folderId).ConfigureAwait(false) : PluginLiveState.Active;
        }

        if (await _UpdateBlockerAsync(manager, folderId).ConfigureAwait(false) is { } blocker)
        {
            return PluginLiveState.Restart(blocker);
        }

        // Autopilot reads a tracker that is withdrawn as gone; here it is withdrawn and registered again at once.
        using var hold = trackers?.HoldChanges();
        if (!prior.Enabled)
        {
            // Disabled but still loaded, waiting for a restart: the update only finishes what the disable began.
            return await _LeaveAsync(manager, folderId).ConfigureAwait(false) ?? PluginLiveState.Active;
        }

        // Found and copied before anything stops, so a version that cannot load costs no downtime.
        var (next, problem) = await _PrepareAsync(folderId).ConfigureAwait(false);
        if (next is null)
        {
            return await _RollBackAsync(manager, running, problem, stopped: false, restartTo: null, failed: null).ConfigureAwait(false);
        }

        // Asked again: a session can start and a workspace can get unsaved work while the new version is prepared.
        if (await _UpdateBlockerAsync(manager, folderId).ConfigureAwait(false) is { } late)
        {
            return PluginLiveState.Restart(late);
        }

        // Turned away up front, so the running version does not stop for bytes that cannot start.
        if (manager.WouldRefuse(next) is { } refused)
        {
            return await _RollBackAsync(manager, running, refused.Reason, stopped: false, restartTo: refused.Status == PluginActivationStatus.NeedsHostRestart ? next : null, failed: next).ConfigureAwait(false);
        }

        if (await _LeaveAsync(manager, folderId).ConfigureAwait(false) is { } stuck)
        {
            return stuck;
        }

        var activated = await _TryActivateAsync(folderId, next).ConfigureAwait(false);
        if (activated.Status is PluginActivationStatus.Activated or PluginActivationStatus.AlreadyActive)
        {
            return PluginLiveState.Active;
        }

        // These bytes can only start with a restart: the running version comes back, and the files and the pin stay on the
        // new ones (approved when the pin moved), so the restart is the route to them and asks for nothing more.
        return await _RollBackAsync(manager, running, activated.Reason, stopped: true, restartTo: activated.Status == PluginActivationStatus.NeedsHostRestart ? next : null, failed: next).ConfigureAwait(false);
    }

    // What a live swap would cut off: what a deactivation would, plus services only a restart replaces and unsaved work.
    private async Task<string?> _UpdateBlockerAsync(PluginManager loaded, string folderId) =>
        await _DeactivationBlockerAsync(loaded, folderId).ConfigureAwait(false)
        ?? (loaded.RegistersServices(folderId) ? "The plugin adds services to the host, which only a restart replaces." : null)
        ?? await loaded.UpdateBlocker(folderId).ConfigureAwait(false);

    private static async Task<PluginLiveState?> _LeaveAsync(PluginManager loaded, string folderId)
    {
        var left = await loaded.DeactivateAsync(folderId).ConfigureAwait(false);
        return left.Status is PluginActivationStatus.Deactivated or PluginActivationStatus.NotActive
            ? null
            : PluginLiveState.Restart(left.Reason ?? "The plugin could not be deactivated.");
    }

    // The new version as discovery sees it (every byte hashed), with its verified copy made; or why it cannot load.
    private async Task<(DiscoveredPlugin? Plugin, string? Problem)> _PrepareAsync(string folderId)
    {
        try
        {
            var found = (await bootstrap.DiscoverAsync(AbstractionsContract.Version, CancellationToken.None).ConfigureAwait(false)).FirstOrDefault(plugin => plugin.FolderId == folderId);
            if (found is null)
            {
                return (null, "The plugin is no longer on disk.");
            }

            if (found.Decision != PluginLoadDecision.Load)
            {
                return (null, $"The plugin load decision is {found.Decision}.");
            }

            await Task.Run(() => PluginShadow.GetOrPrepare(found, fresh: true)).ConfigureAwait(false);
            return (found, null);
        }
        catch (Exception exception) when (exception is PluginIntegrityException or IOException or UnauthorizedAccessException)
        {
            return (null, exception.Message);
        }
    }

    // The previous version runs again from its verified copy, with its folder and pin put back, so a restart asks for approval
    // rather than load the new bytes. restartTo (bytes only a restart can start): folder and pin stay on it for that restart.
    private async Task<PluginLiveState> _RollBackAsync(PluginManager loaded, DiscoveredPlugin previous, string? error, bool stopped, DiscoveredPlugin? restartTo, DiscoveredPlugin? failed)
    {
        var failure = (error ?? "The new version could not be activated.").TrimEnd('.');
        if (stopped)
        {
            // Whatever the failed attempt registered before it threw goes first.
            var cleared = await loaded.DeactivateAsync(previous.FolderId).ConfigureAwait(false);
            var restored = cleared.Status is PluginActivationStatus.Deactivated or PluginActivationStatus.NotActive
                ? await _ActivateWithinAsync(loaded, previous).ConfigureAwait(false)
                : cleared;
            if (restored.Status is not (PluginActivationStatus.Activated or PluginActivationStatus.AlreadyActive))
            {
                return PluginLiveState.UpdateFailed($"The update failed ({failure}) and version {previous.Manifest.Version} could not be brought back ({restored.Reason?.TrimEnd('.')}).");
            }
        }

        if (restartTo is not null)
        {
            return PluginLiveState.Restart($"Version {restartTo.Manifest.Version} needs a restart to start ({failure}). Version {previous.Manifest.Version} keeps running until then.");
        }

        if (failed is not null)
        {
            PluginShadow.MarkFailed(failed);
        }

        await registrations.SaveAsync(previous.FolderId, new PluginRegistration(Enabled: true, PinnedSha256: previous.Sha256), CancellationToken.None).ConfigureAwait(false);
        var folderBack = false;
        try
        {
            folderBack = await installer.RestoreFolderAsync(previous.FolderId, PluginShadow.GetOrPrepare(previous).Folder, previous.Sha256, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is PluginIntegrityException or IOException or UnauthorizedAccessException)
        {
            // Reported below as the folder not coming back.
        }

        return folderBack
            ? PluginLiveState.UpdateRolledBack($"The update could not be activated ({failure}). Version {previous.Manifest.Version} is running.")
            : PluginLiveState.UpdateFailed($"The update could not be activated ({failure}). Version {previous.Manifest.Version} is running, but its files could not be put back; it needs approval after a restart.");
    }

    // A plugin that never finishes Initialize must not hold the gate and the tracker hold for good.
    private readonly Func<DiscoveredPlugin, TimeSpan> _startGrace = startGrace ?? (_ => TimeSpan.FromSeconds(30));

    private async Task<PluginActivationResult> _ActivateWithinAsync(PluginManager loaded, DiscoveredPlugin plugin)
    {
        try
        {
            return await Task.Run(() => loaded.ActivateOne(plugin)).WaitAsync(_startGrace(plugin)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            var reason = $"The plugin did not finish starting in {_startGrace(plugin).TotalSeconds:0.#} seconds.";
            await loaded.AbandonActivationAsync(plugin, reason).ConfigureAwait(false);
            return new PluginActivationResult(PluginActivationStatus.NeedsRestart, reason);
        }
    }

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
    private async Task<PluginLiveState> _ActivateAsync(string folderId, CancellationToken cancellationToken = default)
    {
        var result = await _TryActivateAsync(folderId, null, cancellationToken).ConfigureAwait(false);
        return result.Status is PluginActivationStatus.Activated or PluginActivationStatus.AlreadyActive
            ? PluginLiveState.Active
            : PluginLiveState.Restart(result.Reason ?? "The plugin could not be activated.");
    }

    private async Task<PluginActivationResult> _TryActivateAsync(string folderId, DiscoveredPlugin? found, CancellationToken cancellationToken = default)
    {
        if (manager is null)
        {
            return new(PluginActivationStatus.NeedsRestart, "This host cannot activate a plugin while it runs.");
        }

        try
        {
            // AC-2025: a plugin that loads is hashed from every byte, not from display-cached file hashes.
            var discovered = found ?? (await bootstrap.DiscoverAsync(AbstractionsContract.Version, cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(plugin => plugin.FolderId == folderId);
            return discovered is null
                ? new(PluginActivationStatus.NeedsRestart, "The plugin is no longer on disk.")
                : await _ActivateWithinAsync(manager, discovered).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new(PluginActivationStatus.NeedsRestart, exception.Message);
        }
    }

    // The marker goes first, so a deactivation that has to wait for a restart still removes the folder on that start.
    public async Task<PluginLiveState> RemoveAsync(string folderId, CancellationToken cancellationToken = default)
    {
        await _changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await installer.MarkForRemovalAsync(folderId, cancellationToken).ConfigureAwait(false);
            await registrations.RemoveAsync(folderId, cancellationToken).ConfigureAwait(false);
            return await _DeactivateAsync(folderId).ConfigureAwait(false);
        }
        finally
        {
            _changes.Release();
        }
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
            failures.LastOrDefault(failure => failure.Phase == "mcp-server")?.Error,
            manager?.LoadedParts.FirstOrDefault(entry => entry.Discovered.FolderId == plugin.FolderId).Discovered?.Manifest.Version);
    }
}
