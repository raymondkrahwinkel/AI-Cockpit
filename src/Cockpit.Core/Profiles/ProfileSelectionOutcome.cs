namespace Cockpit.Core.Profiles;

// What a caller should do next after `ProfileSelector` evaluated the available
// profiles for starting a new session.
public enum ProfileSelectionKind
{
    // No profile is usable — guide the user through `claude /login`.
    LoginRequired,

    // Exactly one usable profile — use it without asking.
    UseSilently,

    // More than one usable profile — the caller must ask the user to pick one.
    RequiresChoice,
}

// Result of `ProfileSelector.Select`: either a single profile to use silently,
// a set of candidates to choose from, or a signal that no profile can be used yet.
public sealed record ProfileSelectionOutcome(ProfileSelectionKind Kind, SessionProfile? SingleProfile, IReadOnlyList<SessionProfile> Candidates);
