using Cockpit.Core.Plugins;

namespace Cockpit.Core.Abstractions.Plugins;

/// <summary>Persists the configured plugin stores (#14, AC-7) — the <c>pluginStores</c> list in <c>cockpit.json</c> the manager browses, each a remote (public or private) or a local folder.</summary>
public interface IPluginStoreConfigStore
{
    Task<IReadOnlyList<PluginStoreConfig>> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Adds a store if one with the same kind and location is not already present (case-insensitive); replaces an existing one at the same location so its token can be updated.</summary>
    Task AddAsync(PluginStoreConfig store, CancellationToken cancellationToken = default);

    Task RemoveAsync(PluginStoreConfig store, CancellationToken cancellationToken = default);
}
