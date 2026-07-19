using Cockpit.Core.Secrets;

namespace Cockpit.Core.Abstractions.Secrets;

/// <summary>
/// Loads and persists <see cref="ScreenLockSettings"/> in <c>cockpit.json</c> (the same file the profiles and layout
/// live in). When nothing was ever saved, <see cref="LoadAsync"/> returns the default — locking with the OS is on.
/// </summary>
public interface IScreenLockSettingsStore
{
    Task<ScreenLockSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(ScreenLockSettings settings, CancellationToken cancellationToken = default);
}
