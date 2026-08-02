using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Notifications;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Notifications;

// Persists `NotificationSettings` under the `notifications` section of
// `cockpit.json` (same file/pattern as `SessionProfileStore`). Reads-modifies-writes the
// whole file via `CockpitConfigFileAccess` so it leaves the `profiles` section
// untouched. When no settings were ever saved, `LoadAsync` returns the defaults.
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
