using Cockpit.Core.Abstractions;
using Cockpit.Core.Swarm;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Swarm;

// AC-1916: persists `SwarmSettings` under the `Swarm` section of `cockpit.json`, the way `NodeEndpointSettingsStore`
// persists `NodeEndpoint` — through `CockpitConfigFileAccess`, so every other section stays untouched.
internal sealed class SwarmSettingsStore : ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public SwarmSettingsStore()
        : this(CockpitConfigPath.Default)
    {
    }

    // Test seam: point the store at an arbitrary config file path.
    internal SwarmSettingsStore(string configFilePath)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath);
    }

    public async Task<SwarmSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        return configFile?.Swarm?.ToDomain() ?? SwarmSettings.Default;
    }

    public Task SaveAsync(SwarmSettings settings, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(file => file.Swarm = SwarmSettingsEntry.FromDomain(settings), cancellationToken);

    internal Task UpdateAsync(Func<SwarmSettings, SwarmSettings> update, CancellationToken ct = default) =>
        _configFile.UpdateAsync(file => file.Swarm = SwarmSettingsEntry.FromDomain(update(file.Swarm?.ToDomain() ?? SwarmSettings.Default)), ct);
}
