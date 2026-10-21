using Cockpit.Core.Abstractions;
using Cockpit.Core.HeavyRuns;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.HeavyRuns;

// Persists `HeavyRunSettings` under the `heavyRuns` section of `cockpit.json`, the `DebugSettingsStore` pattern.
internal sealed class HeavyRunSettingsStore : IHeavyRunSettingsStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile = new(CockpitConfigPath.Default);

    public async Task<HeavyRunSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        return configFile?.HeavyRuns?.ToDomain() ?? new HeavyRunSettings();
    }

    public Task SaveAsync(HeavyRunSettings settings, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(
            file => file.HeavyRuns = HeavyRunSettingsEntry.FromDomain(settings),
            cancellationToken);
}
