using Cockpit.Core.Debugging;

namespace Cockpit.Core.Abstractions.Debugging;

/// <summary>
/// Loads and persists <see cref="DebugSettings"/> in <c>cockpit.json</c> (the same config file the profiles and
/// layout live in). When nothing was ever saved, <see cref="LoadAsync"/> returns the defaults.
/// </summary>
public interface IDebugSettingsStore
{
    Task<DebugSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(DebugSettings settings, CancellationToken cancellationToken = default);
}
