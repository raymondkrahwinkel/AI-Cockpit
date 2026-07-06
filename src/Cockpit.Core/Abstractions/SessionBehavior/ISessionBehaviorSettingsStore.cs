using Cockpit.Core.SessionBehavior;

namespace Cockpit.Core.Abstractions.SessionBehavior;

/// <summary>
/// Loads and persists <see cref="SessionBehaviorSettings"/> in <c>cockpit.json</c> (the same config
/// file the profiles and notifications live in). When no settings were ever saved,
/// <see cref="LoadAsync"/> returns the defaults.
/// </summary>
public interface ISessionBehaviorSettingsStore
{
    Task<SessionBehaviorSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(SessionBehaviorSettings settings, CancellationToken cancellationToken = default);
}
