using Cockpit.Core.Profiles;

namespace Cockpit.Core.Abstractions.Sessions;

/// <summary>
/// Installs the statusline Claude Code reports a session's limits through — how full the context window is, and
/// how much of the five-hour and weekly allowance is gone. Those numbers reach Claude Code in response headers
/// the cockpit never sees, and the statusline's JSON is the only place they are readable.
/// <para>
/// An injected seam rather than a static call, because installing it writes a script and a file: a unit test
/// that exercises the launcher should not leave anything in the operator's config directory, and a session that
/// wants no relay simply gets none.
/// </para>
/// </summary>
public interface IStatusLineRelay
{
    /// <summary>
    /// Prepares the relay for one session: adds the environment variable naming its snapshot file to
    /// <paramref name="environment"/>, and returns that file plus the <c>--settings</c> JSON pointing Claude's
    /// statusline at the relay script. Both are null when no relay can be installed (Windows) — the session then
    /// reports no limits, which is honest.
    /// </summary>
    (string? StatusFile, string? SettingsJson) Install(
        SessionProfile? profile,
        string userProfileDirectory,
        IDictionary<string, string?> environment);
}
