using Cockpit.Core.Plugins;

namespace Cockpit.Core.Abstractions.Plugins;

/// <summary>
/// Lists the credential-free store catalog that a remote plugin administrator may present to its operator.
/// </summary>
public interface IPluginStoreCatalog
{
    Task<IReadOnlyList<PluginStoreCatalogEntry>> GetStoresAsync(CancellationToken cancellationToken = default);
}

public sealed record PluginStoreCatalogEntry(string Id, string Location, PluginStoreIndex? Index);
