using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.UsagePill;
using Cockpit.Core.UsagePill;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.UsagePill;

/// <summary>
/// Persists <see cref="UsagePillSettings"/> under the <c>usagePill</c> section of <c>cockpit.json</c>
/// (same file/pattern as <c>TranscriptDisplaySettingsStore</c>). Reads-modifies-writes the whole file via
/// <see cref="CockpitConfigFileAccess"/> so it leaves the other sections untouched. When no settings were
/// ever saved, <see cref="LoadAsync"/> returns the defaults.
/// </summary>
internal sealed class UsagePillSettingsStore : IUsagePillSettingsStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public UsagePillSettingsStore()
        : this(CockpitConfigPath.Default)
    {
    }

    /// <summary>Test seam: point the store at an arbitrary config file path.</summary>
    internal UsagePillSettingsStore(string configFilePath)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath);
    }

    public async Task<UsagePillSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        return configFile?.UsagePill?.ToDomain() ?? new UsagePillSettings();
    }

    public Task SaveAsync(UsagePillSettings settings, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(
            file => file.UsagePill = UsagePillSettingsEntry.FromDomain(settings),
            cancellationToken);
}
