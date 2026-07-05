using Zyra.Voice.Core.Profiles;

namespace Zyra.Voice.Core.Abstractions.Profiles;

/// <summary>
/// Loads and persists the set of <see cref="ClaudeProfile"/>s the cockpit knows about.
/// When no persisted profiles exist, <see cref="LoadAsync"/> auto-detects them from known
/// config directories (see the infrastructure implementation).
/// </summary>
public interface IClaudeProfileStore
{
    /// <summary>
    /// Returns the persisted profiles, or an auto-detected default set if none were ever saved.
    /// </summary>
    Task<IReadOnlyList<ClaudeProfile>> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Persists the given profiles, replacing whatever was stored before.</summary>
    Task SaveAsync(IReadOnlyList<ClaudeProfile> profiles, CancellationToken cancellationToken = default);
}
