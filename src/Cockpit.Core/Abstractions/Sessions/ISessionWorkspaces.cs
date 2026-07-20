namespace Cockpit.Core.Abstractions.Sessions;

/// <summary>
/// The directories the cockpit's own sessions are working in (#67). Delegation asks this because a task handed to
/// another profile may run where the session that handed it over is already working: that session can read and write
/// there itself, so letting the sub-agent it started do the same reaches nothing new — while refusing it made
/// delegation from a session in a repository impossible, which is the only place anyone delegates from.
/// <para>
/// It is deliberately not "any directory": a profile's own <c>AllowedWorkingDirs</c> still governs everywhere else,
/// so delegation cannot be used to reach a part of the disk no session of yours is in.
/// </para>
/// </summary>
public interface ISessionWorkspaces
{
    IReadOnlyList<string> ActiveWorkingDirectories { get; }

    /// <summary>The directory a single session (by its pane id) is working in, or null — so delegation can scope a caller to its own directory (AC-128) rather than granting every open session's.</summary>
    string? WorkingDirectoryForPane(string paneId);
}

/// <summary>No sessions, so nothing is granted on their account — what a consumer without a cockpit (tests, headless tools) sees.</summary>
public sealed class NoSessionWorkspaces : ISessionWorkspaces
{
    public static readonly NoSessionWorkspaces Instance = new();

    public IReadOnlyList<string> ActiveWorkingDirectories => [];

    public string? WorkingDirectoryForPane(string paneId) => null;
}
