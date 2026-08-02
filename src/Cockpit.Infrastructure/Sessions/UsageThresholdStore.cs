using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Sessions;

// Persists the operator's usage thresholds under the `usageThresholds` section of `cockpit.json`
// (AC-233), read-modify-write like every other section so it leaves the rest of the file untouched.
internal sealed class UsageThresholdStore : IUsageThresholdStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public UsageThresholdStore()
        : this(CockpitConfigPath.Default)
    {
    }

    // Test seam: point the store at an arbitrary config file path.
    internal UsageThresholdStore(string configFilePath)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath);
    }

    public async Task<UsageThresholdSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);

        return configFile?.UsageThresholds is { } stored
            ? new UsageThresholdSettings { ByProvider = stored.ByProvider, ByProfile = stored.ByProfile }
            : new UsageThresholdSettings();
    }

    public Task SaveAsync(UsageThresholdSettings settings, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(
            file => file.UsageThresholds = new UsageThresholdSettingsEntry
            {
                ByProvider = settings.ByProvider,
                ByProfile = settings.ByProfile,
            },
            cancellationToken);
}
