using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Plugins;

/// <summary>
/// Persists the <c>pluginStores</c> list of <c>cockpit.json</c> (#14, AC-7) via the shared read-modify-write, so
/// it never clobbers a sibling section. A store is a <see cref="PluginStoreConfig"/> — remote (public or private)
/// or local. Add replaces an entry at the same location (so a token can be updated) and is otherwise additive;
/// remove drops the store at that location.
/// </summary>
/// <remarks>
/// <see cref="LoadAsync"/> also owns the #43 seed-once behavior for <see cref="DefaultStoreUrl"/>: on a
/// genuine first run (empty list, unmarked) it adds the default store and sets the marker; on an existing
/// install that already had stores when this shipped (non-empty list, unmarked) it only sets the marker,
/// so nothing is added behind the operator's back. Once marked, removing the default store is durable —
/// the next load never re-adds it.
/// </remarks>
internal sealed class PluginStoreConfigStore : IPluginStoreConfigStore, ISingletonService
{
    /// <summary>Cockpit's own public plugin store (#43), seeded once so it is available out of the box.</summary>
    internal const string DefaultStoreUrl = "https://github.com/raymondkrahwinkel/AI-Cockpit-Plugins";

    private readonly CockpitConfigFileAccess _configFile;

    public PluginStoreConfigStore()
        : this(CockpitConfigPath.Default)
    {
    }

    /// <summary>Test seam: point the store at an arbitrary config file path.</summary>
    internal PluginStoreConfigStore(string configFilePath)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath);
    }

    public async Task<IReadOnlyList<PluginStoreConfig>> LoadAsync(CancellationToken cancellationToken = default)
    {
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (configFile is not null && configFile.PluginStoresDefaultSeeded)
        {
            return _Clean(configFile.PluginStores);
        }

        if (configFile is not null && configFile.PluginStores.Count > 0)
        {
            // Predates the #43 marker but already has its own stores — mark seeded without adding anything,
            // so this install never gets the default unsolicited even if the list is emptied out later.
            await _configFile.UpdateAsync(file => file.PluginStoresDefaultSeeded = true, cancellationToken).ConfigureAwait(false);
            return _Clean(configFile.PluginStores);
        }

        // Genuine first run: no stores configured yet and never seeded before.
        await _configFile.UpdateAsync(file =>
        {
            if (file.PluginStores.Count == 0)
            {
                file.PluginStores.Add(PluginStoreConfig.Remote(DefaultStoreUrl));
            }

            file.PluginStoresDefaultSeeded = true;
        }, cancellationToken).ConfigureAwait(false);

        return [PluginStoreConfig.Remote(DefaultStoreUrl)];
    }

    public Task AddAsync(PluginStoreConfig store, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(file =>
        {
            file.PluginStores.RemoveAll(existing => existing is not null && existing.SameStoreAs(store));
            file.PluginStores.Add(store);
        }, cancellationToken);

    public Task RemoveAsync(PluginStoreConfig store, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(
            file => file.PluginStores.RemoveAll(existing => existing is not null && existing.SameStoreAs(store)),
            cancellationToken);

    // A hand-edited config can leave a null (a malformed entry the converter could not read) — drop those rather
    // than hand a null store to the browse loop.
    private static IReadOnlyList<PluginStoreConfig> _Clean(IEnumerable<PluginStoreConfig> stores) =>
        stores.Where(store => store is not null).ToList();
}
