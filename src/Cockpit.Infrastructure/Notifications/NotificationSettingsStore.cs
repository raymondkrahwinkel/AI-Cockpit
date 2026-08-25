using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Notifications;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Notifications;

// Persists `NotificationSettings` under the `notifications` section of `cockpit.json` (same
// pattern as `SessionProfileStore`), read-modify-write via `CockpitConfigFileAccess` so the
// `profiles` section stays untouched. `LoadAsync` returns defaults when nothing was ever saved.
internal sealed class NotificationSettingsStore : INotificationSettingsStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public NotificationSettingsStore()
        : this(CockpitConfigPath.Default)
    {
    }

    // Test seam: point the store at an arbitrary config file path.
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
