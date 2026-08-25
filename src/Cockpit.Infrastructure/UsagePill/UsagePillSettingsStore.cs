using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.UsagePill;
using Cockpit.Core.UsagePill;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.UsagePill;

// Persists `UsagePillSettings` under the `usagePill` section of `cockpit.json` (same file/pattern
// as `TranscriptDisplaySettingsStore`), reading-modifying-writing the whole file so other sections
// stay untouched. When no settings were ever saved, `LoadAsync` returns the defaults.
internal sealed class UsagePillSettingsStore : IUsagePillSettingsStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public UsagePillSettingsStore()
        : this(CockpitConfigPath.Default)
    {
    }

    // Test seam: point the store at an arbitrary config file path.
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
