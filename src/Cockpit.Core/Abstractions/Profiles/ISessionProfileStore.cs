using Cockpit.Core.Profiles;

namespace Cockpit.Core.Abstractions.Profiles;

/// <summary>
/// Loads and persists the set of <see cref="SessionProfile"/>s the cockpit knows about.
/// When no persisted profiles exist, <see cref="LoadAsync"/> auto-detects them from known
/// config directories (see the infrastructure implementation).
/// </summary>
public interface ISessionProfileStore
{
    /// <summary>Refreshes live profile decisions after a successful save.</summary>
    event Func<IReadOnlyList<SessionProfile>, Task>? Saved { add { } remove { } }

    /// <summary>
    /// Returns the persisted profiles, or an auto-detected default set if none were ever saved.
    /// </summary>
    Task<IReadOnlyList<SessionProfile>> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists the given profiles, replacing whatever was stored before.
    /// </summary>
    Task SaveAsync(IReadOnlyList<SessionProfile> profiles, CancellationToken cancellationToken = default);
}
