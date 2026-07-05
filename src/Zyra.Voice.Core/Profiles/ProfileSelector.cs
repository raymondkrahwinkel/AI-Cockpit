namespace Zyra.Voice.Core.Profiles;

/// <summary>
/// Pure new-session profile-choice logic (no UI, no I/O), per the C-cockpit UX rule:
/// more than one usable profile requires an explicit choice; exactly one is used silently;
/// none logged in signals that <c>claude /login</c> is needed first.
/// </summary>
public static class ProfileSelector
{
    /// <summary>
    /// Decides what a "new session" UI should do given the known profiles and their login
    /// state. Only logged-in profiles are eligible — an unauthenticated profile cannot spawn
    /// a usable session, so it is excluded from the choice/silent-use candidates.
    /// </summary>
    /// <param name="statuses">Every known profile with its current login state.</param>
    /// <param name="lastUsedLabel">
    /// Label of the last-used profile, if any. When it is among the logged-in candidates and
    /// there is more than one, it is moved to the front of <see cref="ProfileSelectionOutcome.Candidates"/>
    /// as the suggested default — the caller still asks, per the UX rule for >1 profile.
    /// </param>
    public static ProfileSelectionOutcome Select(IReadOnlyList<ClaudeProfileStatus> statuses, string? lastUsedLabel = null)
    {
        var loggedIn = statuses.Where(s => s.IsLoggedIn).Select(s => s.Profile).ToList();

        if (loggedIn.Count == 0)
        {
            return new ProfileSelectionOutcome(ProfileSelectionKind.LoginRequired, null, []);
        }

        if (loggedIn.Count == 1)
        {
            return new ProfileSelectionOutcome(ProfileSelectionKind.UseSilently, loggedIn[0], loggedIn);
        }

        if (lastUsedLabel is not null)
        {
            var lastUsedIndex = loggedIn.FindIndex(p => p.Label == lastUsedLabel);
            if (lastUsedIndex > 0)
            {
                var lastUsed = loggedIn[lastUsedIndex];
                loggedIn.RemoveAt(lastUsedIndex);
                loggedIn.Insert(0, lastUsed);
            }
        }

        return new ProfileSelectionOutcome(ProfileSelectionKind.RequiresChoice, null, loggedIn);
    }
}
