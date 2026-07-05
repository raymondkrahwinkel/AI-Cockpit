namespace Cockpit.Core.Profiles;

/// <summary>
/// A <see cref="ClaudeProfile"/> combined with its current login state, as observed by
/// checking whether that profile's <c>.credentials.json</c> exists (existence only —
/// its contents are never read).
/// </summary>
public sealed record ClaudeProfileStatus(ClaudeProfile Profile, bool IsLoggedIn);
