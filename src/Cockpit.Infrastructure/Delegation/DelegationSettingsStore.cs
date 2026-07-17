using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Core.Delegation;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Delegation;

/// <summary>
/// Persists <see cref="DelegationSettings"/> under the <c>delegation</c> section of <c>cockpit.json</c> (same
/// file/pattern as <see cref="Debugging.DebugSettingsStore"/>). Reads-modifies-writes the whole file via
/// <see cref="CockpitConfigFileAccess"/> so it leaves the other sections untouched.
/// </summary>
internal sealed class DelegationSettingsStore : IDelegationSettingsStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public DelegationSettingsStore()
        : this(CockpitConfigPath.Default)
    {
    }

    /// <summary>Test seam: point the store at an arbitrary config file path.</summary>
    internal DelegationSettingsStore(string configFilePath)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath);
    }

    public async Task<DelegationSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        return configFile?.Delegation?.ToDomain() ?? new DelegationSettings();
    }

    public Task SaveAsync(DelegationSettings settings, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(
            file => file.Delegation = DelegationSettingsEntry.FromDomain(settings),
            cancellationToken);
}
