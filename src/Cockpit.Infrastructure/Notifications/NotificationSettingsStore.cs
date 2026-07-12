using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Notifications;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Notifications;

/// <summary>
/// Persists <see cref="NotificationSettings"/> under the <c>notifications</c> section of
/// <c>cockpit.json</c> (same file/pattern as <c>SessionProfileStore</c>). Reads-modifies-writes the
/// whole file via <see cref="CockpitConfigFileAccess"/> so it leaves the <c>profiles</c> section
/// untouched. When no settings were ever saved, <see cref="LoadAsync"/> returns the defaults.
/// </summary>
internal sealed class NotificationSettingsStore : INotificationSettingsStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public NotificationSettingsStore()
        : this(CockpitConfigPath.Default)
    {
    }

    /// <summary>Test seam: point the store at an arbitrary config file path.</summary>
    internal NotificationSettingsStore(string configFilePath)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath);
    }

    public async Task<NotificationSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        return configFile?.Notifications?.ToDomain() ?? new NotificationSettings();
    }

    public Task SaveAsync(NotificationSettings settings, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(
            file => file.Notifications = NotificationSettingsEntry.FromDomain(settings),
            cancellationToken);
}
