namespace Cockpit.Core.Profiles;

// AC-1357: one profile's sign-in as the poll saw it. `ExpiredSince` is when a poll first read it signed out, not when
// the provider's credential actually ran out — the check is existence-only and cannot know that.
public sealed record ProfileLoginHealth(string Profile, bool SignedIn, DateTimeOffset LastCheck, DateTimeOffset? ExpiredSince);
