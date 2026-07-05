using Cockpit.Core.Claude.Permissions;

namespace Cockpit.Core.Abstractions.Claude;

/// <summary>
/// Persists the always-allow permission rules per profile in the shared cockpit config, so a
/// "never ask me again" choice survives across app restarts. Rules are scoped by profile label —
/// a work profile's allowances never leak into a personal one.
/// </summary>
public interface IPermissionRuleStore
{
    /// <summary>
    /// Loads the saved rules for the profile identified by <paramref name="profileLabel"/>, or an
    /// empty list when that profile has none yet (or when <paramref name="profileLabel"/> is null —
    /// a profile-less session has no persistent identity to key rules on).
    /// </summary>
    Task<IReadOnlyList<PermissionRule>> LoadAsync(string? profileLabel, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists <paramref name="rule"/> for the profile identified by <paramref name="profileLabel"/>,
    /// merging with (not replacing) that profile's existing rules and leaving every other section of
    /// the config — and every other profile's rules — untouched. A null <paramref name="profileLabel"/>
    /// is a no-op: there is no stable key to persist against.
    /// </summary>
    Task AddAsync(string? profileLabel, PermissionRule rule, CancellationToken cancellationToken = default);
}
