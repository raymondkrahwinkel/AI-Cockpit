namespace Cockpit.Core.Profiles;

// A `SessionProfile` combined with its current login state, as observed by
// checking whether that profile's `.credentials.json` exists (existence only —
// its contents are never read).
public sealed record SessionProfileStatus(SessionProfile Profile, bool IsLoggedIn);
