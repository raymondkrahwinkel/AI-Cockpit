namespace Cockpit.Core.Abstractions.Sessions;

/// <summary>
/// A TTY session that reports where its statusline snapshots land — the file the cockpit reads a session's
/// context/rate-limit percentages from (written by the provider plugin owning the statusline). A capability of
/// the launched process, not a wider launch contract: only a session started with the relay has one.
/// </summary>
public interface ITtyStatusFile
{
    /// <summary>
    /// The file Claude's statusline JSON is written to for this session, or null when no relay was installed (Windows).
    /// </summary>
    string? StatusFile { get; }
}
