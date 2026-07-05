using Cockpit.Core.SessionSwitching;

namespace Cockpit.Core.Abstractions.SessionSwitching;

/// <summary>
/// Loads and persists <see cref="SessionSwitchSettings"/> in <c>cockpit.json</c> (the same config
/// file the profiles and notifications live in). When no settings were ever saved,
/// <see cref="LoadAsync"/> returns the defaults.
/// </summary>
public interface ISessionSwitchSettingsStore
{
    Task<SessionSwitchSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(SessionSwitchSettings settings, CancellationToken cancellationToken = default);
}
