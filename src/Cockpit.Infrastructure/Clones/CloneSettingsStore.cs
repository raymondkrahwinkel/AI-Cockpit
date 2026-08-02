using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Clones;
using Cockpit.Core.Clones;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Clones;

// Persists `CloneSettings` under the `cloneSettings` section of `cockpit.json`, going through
// `CockpitConfigFileAccess` so it leaves the other sections — including the clones registry — untouched
// (same pattern as `WorktreeSettingsStore`).
internal sealed class CloneSettingsStore : ICloneSettingsStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public CloneSettingsStore()
        : this(CockpitConfigPath.Default)
    {
    }

    // Test seam: point the store at an arbitrary config file path.
    internal CloneSettingsStore(string configFilePath)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath);
    }

    public string DefaultRoot => CockpitConfigPath.ClonesRoot;

    public async Task<CloneSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        return configFile?.CloneSettings?.ToDomain() ?? new CloneSettings();
    }

    public Task SaveAsync(CloneSettings settings, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(
            file => file.CloneSettings = CloneSettingsEntry.FromDomain(settings),
            cancellationToken);
}
