namespace Cockpit.Core.Abstractions.Plugins;

/// <summary>Persists the configured plugin-store URLs (#14) — the <c>pluginStores</c> list in <c>cockpit.json</c> the manager browses.</summary>
public interface IPluginStoreConfigStore
{
    Task<IReadOnlyList<string>> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Adds a store URL if it is not already present (case-insensitive); a no-op otherwise.</summary>
    Task AddAsync(string storeUrl, CancellationToken cancellationToken = default);

    Task RemoveAsync(string storeUrl, CancellationToken cancellationToken = default);
}
