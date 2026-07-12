using Cockpit.Core.Profiles;

namespace Cockpit.Core.Abstractions.Profiles;

/// <summary>
/// Checks whether a <see cref="SessionProfile"/> is logged in. Existence-only: never reads
/// the credentials file's contents (Iron Law #8 — do not print/inspect secret values).
/// </summary>
public interface IClaudeProfileLoginChecker
{
    /// <summary>True if <c>&lt;profile.ConfigDir&gt;\.credentials.json</c> exists.</summary>
    bool IsLoggedIn(SessionProfile profile);
}
