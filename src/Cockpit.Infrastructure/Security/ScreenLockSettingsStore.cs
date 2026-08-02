using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Secrets;
using Cockpit.Core.Secrets;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Security;

// Persists `ScreenLockSettings` under the `ScreenLock` section of `cockpit.json` (same
// file/pattern as `Cockpit.Infrastructure.Delegation.DelegationSettingsStore`). Reads-modifies-writes
// the whole file via `CockpitConfigFileAccess` so it leaves the other sections untouched.
internal sealed class ScreenLockSettingsStore : IScreenLockSettingsStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public ScreenLockSettingsStore()
        : this(CockpitConfigPath.Default)
    {
    }

    // Test seam: point the store at an arbitrary config file path.
    internal ScreenLockSettingsStore(string configFilePath)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath);
    }

    public async Task<ScreenLockSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        return configFile?.ScreenLock?.ToDomain() ?? new ScreenLockSettings();
    }

    public Task SaveAsync(ScreenLockSettings settings, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(
            file => file.ScreenLock = ScreenLockSettingsEntry.FromDomain(settings),
            cancellationToken);
}
