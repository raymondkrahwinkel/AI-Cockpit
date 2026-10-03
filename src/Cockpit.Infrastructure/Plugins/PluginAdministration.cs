using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Plugins;
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
    PluginDiagnostics? diagnostics = null) : IPluginAdministration
{
    public async Task<IReadOnlyList<InstalledPlugin>> GetInstalledAsync(CancellationToken cancellationToken = default)
    {
        var discovered = await bootstrap.DiscoverAsync(AbstractionsContract.Version, cancellationToken).ConfigureAwait(false);
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
        }

        return result;
    }

    public Task SetEnabledAsync(string folderId, bool enabled, string pinnedSha256, CancellationToken cancellationToken = default) =>
        registrations.SaveAsync(folderId, new PluginRegistration(Enabled: enabled, PinnedSha256: pinnedSha256), cancellationToken);

    public async Task RemoveAsync(string folderId, CancellationToken cancellationToken = default)
    {
        await installer.MarkForRemovalAsync(folderId, cancellationToken).ConfigureAwait(false);
        await registrations.RemoveAsync(folderId, cancellationToken).ConfigureAwait(false);
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
