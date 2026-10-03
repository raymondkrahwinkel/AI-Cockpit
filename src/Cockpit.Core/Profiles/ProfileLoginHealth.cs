namespace Cockpit.Core.Profiles;

// AC-1357: one profile's sign-in as the poll saw it. `ExpiredSince` is when a poll first read it signed out, not when
// the provider's credential actually ran out — the check is existence-only and cannot know that.
public sealed record ProfileLoginHealth(string Profile, bool SignedIn, DateTimeOffset LastCheck, DateTimeOffset? ExpiredSince)
{
    // AC-1470: the profile's provider id, and whether its provider has a sign-in to check at all.
    public string Provider { get; init; } = "";

    public ProfileSignInKind SignIn { get; init; } = ProfileSignInKind.Unchecked;

    // AC-1470: when the expiry was announced (notification and controller inbox); null while signed in.
    public DateTimeOffset? AnnouncedAt { get; init; }
}

// AC-1470: what the host can say about a sign-in. A provider that declares no sign-in check reads Unchecked, never
// signed in: nothing was asked.
public enum ProfileSignInKind
{
    SignedIn,
    Expired,
    Unchecked,
}
