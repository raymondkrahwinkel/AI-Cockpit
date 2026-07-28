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
    private readonly PluginRegistrationStore _registrationStore;
    private readonly PluginInstaller _installer;
    private readonly string _pluginsRoot;

    public PluginBootstrap()
        : this(CockpitConfigPath.PluginsRoot, CockpitConfigPath.Default)
    {
    }

    /// <summary>Test seam: point discovery and the restart-deferred sweeps at an arbitrary plugins root.</summary>
    internal PluginBootstrap(string pluginsRoot, string configFilePath)
    {
        _pluginsRoot = pluginsRoot;
        _installer = new PluginInstaller(pluginsRoot);
        _registrationStore = new PluginRegistrationStore(configFilePath);
    }

    /// <summary>The plugins root — a <c>plugins/</c> folder next to <c>cockpit.json</c>. Each plugin lives in its own subfolder here.</summary>
    public static string PluginsRoot => CockpitConfigPath.PluginsRoot;

    /// <summary>
    /// The startup pass: applies what the previous session deferred — every staged update swapped into its
    /// folder, every folder marked for removal deleted — and then discovers. The one entry point the app's
    /// composition root uses, so applying cannot be dropped without dropping discovery with it.
    /// </summary>
    /// <remarks>
    /// Both halves used to live in <see cref="DiscoverAsync"/>, which made every rediscovery a write: the
    /// plugin manager rediscovers after each enable/disable/remove, and the background update checker
    /// rediscovers on a fifteen-minute timer. Both therefore swapped staged updates in mid-session — the one
    /// thing the deferral exists to prevent, since a loaded assembly's file is locked on Windows and, on the
    /// platforms where it is not, the replace succeeds under a plugin that is still running (AC-455). Startup
    /// is the only moment at which nothing is loaded, which is why it is the only moment either one runs.
    /// </remarks>
    public async Task<IReadOnlyList<DiscoveredPlugin>> ApplyPendingChangesAndDiscoverAsync(int hostAbstractionsMajor, CancellationToken cancellationToken = default)
    {
        await _installer.SweepPendingUpdatesAsync(cancellationToken).ConfigureAwait(false);
        await _installer.SweepRemovalsAsync(cancellationToken).ConfigureAwait(false);

        return await DiscoverAsync(hostAbstractionsMajor, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Scans <see cref="PluginsRoot"/>, parses each manifest, hashes each entry assembly and runs the load
    /// policy against the saved registrations — returning what to do with every plugin found, loading none
    /// and changing nothing on disk. A plugin marked for removal is left out: its folder survives until the
    /// next start deletes it, but it is gone as far as anything asking here is concerned.
    /// </summary>
    public async Task<IReadOnlyList<DiscoveredPlugin>> DiscoverAsync(int hostAbstractionsMajor, CancellationToken cancellationToken = default)
    {
        var saved = await _registrationStore.LoadAllAsync(cancellationToken).ConfigureAwait(false);
        return await _discovery.DiscoverAsync(_pluginsRoot, saved, hostAbstractionsMajor, cancellationToken).ConfigureAwait(false);
    }
}
