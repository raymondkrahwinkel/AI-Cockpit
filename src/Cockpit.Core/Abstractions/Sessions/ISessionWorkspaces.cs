namespace Cockpit.Core.Abstractions.Sessions;

/// <summary>
/// The directories the cockpit's own sessions are working in (#67). Delegation asks this because a task handed
/// to another profile may run where the handing-over session already works — reaching nothing new, since
/// refusing it would make delegation from a repository impossible. Not "any directory": <c>AllowedWorkingDirs</c> still governs elsewhere.
/// </summary>
public interface ISessionWorkspaces
{
    IReadOnlyList<string> ActiveWorkingDirectories { get; }

    /// <summary>
    /// The directory a single session (by its pane id) is working in, or null — so delegation can scope a caller to its own directory (AC-128) rather than granting every open session's.
    /// </summary>
    string? WorkingDirectoryForPane(string paneId);
}

// No sessions, so nothing is granted on their account — what a consumer without a cockpit (tests, headless tools) sees.
public sealed class NoSessionWorkspaces : ISessionWorkspaces
{
    public static readonly NoSessionWorkspaces Instance = new();

    public IReadOnlyList<string> ActiveWorkingDirectories => [];

    public string? WorkingDirectoryForPane(string paneId) => null;
}
