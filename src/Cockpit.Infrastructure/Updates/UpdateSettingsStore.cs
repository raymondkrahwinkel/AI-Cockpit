using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Updates;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Updates;

/// <summary>Persists <see cref="UpdateSettings"/> under the <c>updates</c> section of <c>cockpit.json</c> (#71) — same pattern as every other section: read-modify-write the whole file, leave the rest alone.</summary>
internal sealed class UpdateSettingsStore : IUpdateSettingsStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public UpdateSettingsStore()
        : this(CockpitConfigPath.Default)
    {
    }

    /// <summary>Test seam: point the store at an arbitrary config file path.</summary>
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
