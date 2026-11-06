using Cockpit.Core.Plugins;

namespace Cockpit.Core.Abstractions.Plugins;

public interface IPluginTrustStore
{
    Task<PluginTrustEntry?> GetAsync(string folderId, CancellationToken cancellationToken = default);
    Task SaveAsync(string folderId, PluginTrustEntry entry, CancellationToken cancellationToken = default);
    Task RemoveAsync(string folderId, CancellationToken cancellationToken = default);
}
