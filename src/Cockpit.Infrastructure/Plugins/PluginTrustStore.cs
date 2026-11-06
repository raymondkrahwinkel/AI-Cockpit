using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Plugins;

internal sealed class PluginTrustStore : IPluginTrustStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _config;

    public PluginTrustStore() : this(CockpitConfigPath.Default) { }

    internal PluginTrustStore(string configFilePath) => _config = new CockpitConfigFileAccess(configFilePath);

    public async Task<PluginTrustEntry?> GetAsync(string folderId, CancellationToken cancellationToken = default) =>
        (await _config.ReadAsync(cancellationToken).ConfigureAwait(false))?.PluginTrust.GetValueOrDefault(folderId);

    public Task SaveAsync(string folderId, PluginTrustEntry entry, CancellationToken cancellationToken = default) =>
        _config.UpdateAsync(config => config.PluginTrust[folderId] = entry, cancellationToken);

    public Task RemoveAsync(string folderId, CancellationToken cancellationToken = default) =>
        _config.UpdateAsync(config => config.PluginTrust.Remove(folderId), cancellationToken);
}
