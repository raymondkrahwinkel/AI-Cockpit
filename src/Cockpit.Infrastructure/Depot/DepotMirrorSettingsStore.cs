using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Depot;
using Cockpit.Core.Depot;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Depot;

// Persists `DepotMirrorSettings` under the `depotMirrorSettings` section of `cockpit.json`, going through
// `CockpitConfigFileAccess` so it leaves the other sections — including the mirrors registry — untouched
// (same pattern as `CloneSettingsStore`).
internal sealed class DepotMirrorSettingsStore : IDepotMirrorSettingsStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public DepotMirrorSettingsStore()
        : this(CockpitConfigPath.Default)
    {
    }

    // Test seam: point the store at an arbitrary config file path.
    internal DepotMirrorSettingsStore(string configFilePath)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath);
    }

    public string DefaultRoot => CockpitConfigPath.DepotMirrorsRoot;

    public async Task<DepotMirrorSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        return configFile?.DepotMirrorSettings?.ToDomain() ?? new DepotMirrorSettings();
    }

    public Task SaveAsync(DepotMirrorSettings settings, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(
            file => file.DepotMirrorSettings = DepotMirrorSettingsEntry.FromDomain(settings),
            cancellationToken);
}
