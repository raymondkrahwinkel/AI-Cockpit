using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.SessionSwitching;
using Cockpit.Core.SessionSwitching;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.SessionSwitching;

/// <summary>
/// Persists <see cref="SessionSwitchSettings"/> under the <c>sessionSwitching</c> section of
/// <c>cockpit.json</c> (same file/pattern as <c>SessionProfileStore</c> and
/// <c>NotificationSettingsStore</c>). Reads-modifies-writes the whole file via
/// <see cref="CockpitConfigFileAccess"/> so it leaves the <c>profiles</c>, <c>notifications</c> and
/// <c>permissionRules</c> sections untouched. When no settings were ever saved,
/// <see cref="LoadAsync"/> returns the defaults.
/// </summary>
internal sealed class SessionSwitchSettingsStore : ISessionSwitchSettingsStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public SessionSwitchSettingsStore()
        : this(CockpitConfigPath.Default)
    {
    }

    /// <summary>Test seam: point the store at an arbitrary config file path.</summary>
    internal SessionSwitchSettingsStore(string configFilePath)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath);
    }

    public async Task<SessionSwitchSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        return configFile?.SessionSwitching?.ToDomain() ?? new SessionSwitchSettings();
    }

    public Task SaveAsync(SessionSwitchSettings settings, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(
            file => file.SessionSwitching = SessionSwitchSettingsEntry.FromDomain(settings),
            cancellationToken);
}
