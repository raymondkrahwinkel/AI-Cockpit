using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Secrets;
using Cockpit.Core.Secrets;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Security;

/// <summary>
/// Persists <see cref="ScreenLockSettings"/> under the <c>ScreenLock</c> section of <c>cockpit.json</c> (same
/// file/pattern as <see cref="Cockpit.Infrastructure.Delegation.DelegationSettingsStore"/>). Reads-modifies-writes
/// the whole file via <see cref="CockpitConfigFileAccess"/> so it leaves the other sections untouched.
/// </summary>
internal sealed class ScreenLockSettingsStore : IScreenLockSettingsStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public ScreenLockSettingsStore()
        : this(CockpitConfigPath.Default)
    {
    }

    /// <summary>Test seam: point the store at an arbitrary config file path.</summary>
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
