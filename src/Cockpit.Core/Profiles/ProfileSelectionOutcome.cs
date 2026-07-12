namespace Cockpit.Core.Profiles;

/// <summary>
/// What a caller should do next after <see cref="ProfileSelector"/> evaluated the available
/// profiles for starting a new session.
/// </summary>
public enum ProfileSelectionKind
{
    /// <summary>No profile is usable — guide the user through <c>claude /login</c>.</summary>
    LoginRequired,

    /// <summary>Exactly one usable profile — use it without asking.</summary>
    UseSilently,

    /// <summary>More than one usable profile — the caller must ask the user to pick one.</summary>
    RequiresChoice,
}

/// <summary>
/// Result of <see cref="ProfileSelector.Select"/>: either a single profile to use silently,
/// a set of candidates to choose from, or a signal that no profile can be used yet.
/// </summary>
public sealed record ProfileSelectionOutcome(ProfileSelectionKind Kind, SessionProfile? SingleProfile, IReadOnlyList<SessionProfile> Candidates);
