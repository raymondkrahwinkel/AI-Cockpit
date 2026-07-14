using Cockpit.Core.Profiles;

namespace Cockpit.Core.Abstractions.Profiles;

/// <summary>
/// Checks whether a <see cref="SessionProfile"/> is logged in. Existence-only: never reads
/// the credentials file's contents (Iron Law #8 — do not print/inspect secret values).
/// </summary>
public interface IClaudeProfileLoginChecker
{
    /// <summary>True if <c>&lt;profile.Claude.ConfigDir&gt;\.credentials.json</c> exists; false for a profile running under another provider, which has no Claude login to check.</summary>
    bool IsLoggedIn(SessionProfile profile);
}
