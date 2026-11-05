using Cockpit.Core.Abstractions;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Plugins;

// Public discovery facade over the internal `PluginDiscovery` and `PluginRegistrationStore` (#14), so the
// app's composition root can enumerate plugins without a broad `InternalsVisibleTo`. Usable via `new` in
// `Program.Main`'s pre-container pass and injectable (singleton) for post-install re-discovery.
public sealed class PluginBootstrap : ISingletonService
{
    private readonly PluginDiscovery _discovery = new();
    private readonly PluginRegistrationStore _registrationStore;
    private readonly PluginInstaller _installer;
    private readonly string _pluginsRoot;

    public PluginBootstrap()
        : this(CockpitConfigPath.PluginsRoot, CockpitConfigPath.Default)
    {
    }

    // Test seam: point discovery and the restart-deferred sweeps at an arbitrary plugins root.
    internal PluginBootstrap(string pluginsRoot, string configFilePath)
    {
        _pluginsRoot = pluginsRoot;
        _installer = new PluginInstaller(pluginsRoot);
        _registrationStore = new PluginRegistrationStore(configFilePath);
    }

    // The plugins root — a `plugins/` folder next to `cockpit.json`. Each plugin lives in its own subfolder here.
    public static string PluginsRoot => CockpitConfigPath.PluginsRoot;

    // AC-455: Startup pass — applies deferred staged updates/removals, then discovers, as one entry point so
    // applying can't be split from discovery. Split out of `DiscoverAsync`, whose frequent mid-session
    // rediscovery (enable/disable/remove, 15-min update timer) swapped files under a still-running plugin.
    public async Task<IReadOnlyList<DiscoveredPlugin>> ApplyPendingChangesAndDiscoverAsync(int hostAbstractionsMajor, CancellationToken cancellationToken = default)
    {
        await _installer.RestoreUnapprovedUpdatesAsync(await _registrationStore.LoadAllAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        await _installer.SweepPendingUpdatesAsync(cancellationToken).ConfigureAwait(false);
        await _installer.SweepRemovalsAsync(cancellationToken).ConfigureAwait(false);
        await ReconcileUpdatesAsync(cancellationToken).ConfigureAwait(false);

        return await DiscoverAsync(hostAbstractionsMajor, cancellationToken).ConfigureAwait(false);
    }

    // AC-2096: an in-place update killed between the folder swap and the re-pin leaves its marker. New bytes on disk and the
    // pin still on the bytes the marker says were replaced: the capability check passed, so the pin moves. Else only the marker goes.
    internal async Task ReconcileUpdatesAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_pluginsRoot))
        {
            return;
        }

        foreach (var marker in Directory.EnumerateFiles(_pluginsRoot, PluginInstaller.UpdateMarkerPrefix + "*"))
        {
            var folderId = Path.GetFileName(marker)[PluginInstaller.UpdateMarkerPrefix.Length..];
            var folder = Path.Combine(_pluginsRoot, folderId);
            var hashes = File.ReadAllLines(marker);
            if (hashes.Length == 2 && hashes[0].Length > 0 && Directory.Exists(folder)
                && string.Equals(await PluginClosureHash.OfInstalledFolderAsync(folder, cancellationToken).ConfigureAwait(false), hashes[1], StringComparison.OrdinalIgnoreCase)
                && (await _registrationStore.LoadAllAsync(cancellationToken).ConfigureAwait(false)).GetValueOrDefault(folderId) is { } prior
                && string.Equals(prior.PinnedSha256, hashes[0], StringComparison.OrdinalIgnoreCase))
            {
                await _registrationStore.SaveAsync(folderId, new PluginRegistration(prior.Enabled, hashes[1]), cancellationToken).ConfigureAwait(false);
            }

            if (hashes.Length != 2 || hashes[0].Length != 0)
            {
                File.Delete(marker);
            }
        }
    }

    // The marker is recovery evidence until the pin names the bytes that are actually on disk.
    internal async Task ConfirmPinnedInPlaceUpdateAsync(string folderId, string pinnedSha256, CancellationToken cancellationToken = default)
    {
        var marker = Path.Combine(_pluginsRoot, PluginInstaller.UpdateMarkerPrefix + folderId);
        if (!File.Exists(marker))
        {
            return;
        }

        var hashes = await File.ReadAllLinesAsync(marker, cancellationToken).ConfigureAwait(false);
        var folder = Path.Combine(_pluginsRoot, folderId);
        if (hashes.Length == 2
            && string.Equals(hashes[1], pinnedSha256, StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(folder)
            && string.Equals(await PluginClosureHash.OfInstalledFolderAsync(folder, cancellationToken).ConfigureAwait(false), pinnedSha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(marker);
        }
    }

    // Scans the plugins root this instance was built with, parses each manifest, hashes each entry assembly
    // and runs the load policy against saved registrations — returning what to do, loading nothing and
    // changing nothing on disk. A plugin marked for removal is left out even though its folder survives.
    public async Task<IReadOnlyList<DiscoveredPlugin>> DiscoverAsync(int hostAbstractionsMajor, CancellationToken cancellationToken = default)
    {
        var saved = await _registrationStore.LoadAllAsync(cancellationToken).ConfigureAwait(false);
        return await _discovery.DiscoverAsync(_pluginsRoot, saved, hostAbstractionsMajor, cancellationToken).ConfigureAwait(false);
    }

    // AC-2018: for a caller that shows or compares versions and loads nothing (Options, the plugin store, the update
    // check). Its decisions are a status to show; what loads is decided by the startup pass above, from every byte.
    public async Task<IReadOnlyList<DiscoveredPlugin>> DiscoverForDisplayAsync(int hostAbstractionsMajor, CancellationToken cancellationToken = default)
    {
        var saved = await _registrationStore.LoadAllAsync(cancellationToken).ConfigureAwait(false);
        return await _discovery.DiscoverAsync(_pluginsRoot, saved, hostAbstractionsMajor, cancellationToken, forDisplay: true).ConfigureAwait(false);
    }
}
