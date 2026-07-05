using Cockpit.Core.Notifications;

namespace Cockpit.Core.Abstractions.Notifications;

/// <summary>
/// Loads and persists <see cref="NotificationSettings"/> in <c>cockpit.json</c> (the same config
/// file the profiles live in). When no settings were ever saved, <see cref="LoadAsync"/> returns
/// the defaults.
/// </summary>
public interface INotificationSettingsStore
{
    Task<NotificationSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(NotificationSettings settings, CancellationToken cancellationToken = default);
}
