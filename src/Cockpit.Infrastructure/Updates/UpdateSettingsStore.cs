using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Updates;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Updates;

// Persists `UpdateSettings` under the `updates` section of `cockpit.json` (#71) — same pattern as every other section: read-modify-write the whole file, leave the rest alone.
internal sealed class UpdateSettingsStore : IUpdateSettingsStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public UpdateSettingsStore()
        : this(CockpitConfigPath.Default)
    {
    }

    // Test seam: point the store at an arbitrary config file path.
    internal UpdateSettingsStore(string configFilePath)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath);
    }

    public async Task<UpdateSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        return configFile?.Updates?.ToDomain() ?? new UpdateSettings();
    }

    public Task SaveAsync(UpdateSettings settings, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(
            file => file.Updates = UpdateSettingsEntry.FromDomain(settings),
            cancellationToken);
}
