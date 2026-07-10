using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Plugins;

/// <summary>
/// Persists the <c>pluginStores</c> list of <c>cockpit.json</c> (#14) via the shared read-modify-write, so
/// it never clobbers a sibling section. Add is idempotent (case-insensitive); remove drops the exact URL.
/// </summary>
internal sealed class PluginStoreConfigStore : IPluginStoreConfigStore, ISingletonService
{
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

    public async Task<IReadOnlyList<string>> LoadAsync(CancellationToken cancellationToken = default)
    {
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        return configFile?.PluginStores ?? [];
    }

    public Task AddAsync(string storeUrl, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(file =>
        {
            if (!file.PluginStores.Any(existing => string.Equals(existing, storeUrl, StringComparison.OrdinalIgnoreCase)))
            {
                file.PluginStores.Add(storeUrl);
            }
        }, cancellationToken);

    public Task RemoveAsync(string storeUrl, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(
            file => file.PluginStores.RemoveAll(existing => string.Equals(existing, storeUrl, StringComparison.OrdinalIgnoreCase)),
            cancellationToken);
}
