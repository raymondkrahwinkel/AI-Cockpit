using Cockpit.Core.Abstractions;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Plugins;

/// <summary>
/// The public discovery facade over the internal <see cref="PluginDiscovery"/> and
/// <see cref="PluginRegistrationStore"/> (#14), so the app's composition root can enumerate plugins
/// without a broad <c>InternalsVisibleTo</c>. Usable via <c>new</c> in <c>Program.Main</c>'s pre-container
/// pass and injectable (singleton) for the plugin manager's live re-discovery after an install.
/// </summary>
public sealed class PluginBootstrap : ISingletonService
{
    private readonly PluginDiscovery _discovery = new();
    private readonly PluginRegistrationStore _registrationStore = new();
    private readonly PluginInstaller _installer = new();

    /// <summary>The plugins root — a <c>plugins/</c> folder next to <c>cockpit.json</c>. Each plugin lives in its own subfolder here.</summary>
    public static string PluginsRoot => CockpitConfigPath.PluginsRoot;

    /// <summary>
    /// Scans <see cref="PluginsRoot"/>, parses each manifest, hashes each entry assembly and runs the load
    /// policy against the saved registrations — returning what to do with every plugin found, loading none.
    /// </summary>
    public async Task<IReadOnlyList<DiscoveredPlugin>> DiscoverAsync(int hostAbstractionsMajor, CancellationToken cancellationToken = default)
    {
        // Apply any staged update and delete any folder the operator removed last session before scanning, so
        // the swap/delete runs while no plugin assembly is loaded (locked) and a removed plugin is never
        // rediscovered or loaded.
        await _installer.SweepPendingUpdatesAsync(cancellationToken).ConfigureAwait(false);
        await _installer.SweepRemovalsAsync(cancellationToken).ConfigureAwait(false);

        var saved = await _registrationStore.LoadAllAsync(cancellationToken).ConfigureAwait(false);
        return await _discovery.DiscoverAsync(PluginsRoot, saved, hostAbstractionsMajor, cancellationToken).ConfigureAwait(false);
    }
}
