namespace Cockpit.Core.Abstractions.Sessions;

/// <summary>
/// A TTY session that reports where its statusline snapshots land — the file the cockpit reads a session's
/// context and rate-limit percentages from (see <c>StatusLineRelay</c>).
/// <para>
/// A capability of the launched process rather than a wider launch contract: only a session started with the
/// relay has one, the callers that do not care never see it, and the ones that do ask the thing that knows.
/// </para>
/// </summary>
public interface ITtyStatusFile
{
    /// <summary>The file Claude's statusline JSON is written to for this session, or null when no relay was installed (Windows).</summary>
    string? StatusFile { get; }
}
