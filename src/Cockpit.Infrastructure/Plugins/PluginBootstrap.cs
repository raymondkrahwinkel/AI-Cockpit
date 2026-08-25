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
        await _installer.SweepPendingUpdatesAsync(cancellationToken).ConfigureAwait(false);
        await _installer.SweepRemovalsAsync(cancellationToken).ConfigureAwait(false);

        return await DiscoverAsync(hostAbstractionsMajor, cancellationToken).ConfigureAwait(false);
    }

    // Scans the plugins root this instance was built with, parses each manifest, hashes each entry assembly
    // and runs the load policy against saved registrations — returning what to do, loading nothing and
    // changing nothing on disk. A plugin marked for removal is left out even though its folder survives.
    public async Task<IReadOnlyList<DiscoveredPlugin>> DiscoverAsync(int hostAbstractionsMajor, CancellationToken cancellationToken = default)
    {
        var saved = await _registrationStore.LoadAllAsync(cancellationToken).ConfigureAwait(false);
        return await _discovery.DiscoverAsync(_pluginsRoot, saved, hostAbstractionsMajor, cancellationToken).ConfigureAwait(false);
    }
}
