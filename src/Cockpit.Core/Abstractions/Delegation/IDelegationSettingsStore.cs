using Cockpit.Core.Delegation;

namespace Cockpit.Core.Abstractions.Delegation;

/// <summary>
/// Loads and persists <see cref="DelegationSettings"/> in <c>cockpit.json</c> (the same config file the profiles and
/// layout live in). When nothing was ever saved, <see cref="LoadAsync"/> returns the defaults.
/// </summary>
public interface IDelegationSettingsStore
{
    Task<DelegationSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(DelegationSettings settings, CancellationToken cancellationToken = default);
}
