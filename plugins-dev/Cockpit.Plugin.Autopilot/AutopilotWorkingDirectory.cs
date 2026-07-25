using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// The directory an Autopilot run works in (AC-174). The operator's chosen folder when they named one at approval (a
/// run planned from a tracker issue has no session, so they pick it in the plan dialog), else the active session's
/// working directory (the run follows what they are looking at), else the cockpit's own working directory — the folder
/// it was launched in. A folder that is a git repository is isolated per step; a plain folder runs without isolation
/// (an admin task with no repo) — that decision is made downstream from this directory, this only resolves where it is.
/// </summary>
internal static class AutopilotWorkingDirectory
{
    public static string Resolve(IWorkspaceContext context, string? chosen)
    {
        var directory = !string.IsNullOrWhiteSpace(chosen)
            ? chosen
            : context.Sessions.ActiveSessionWorkingDirectory is { Length: > 0 } active
                ? active
                : Directory.GetCurrentDirectory();

        // Normalise to a canonical absolute path so the git-status check, the worktree and the confinement all resolve
        // the same directory — a relative or non-normalised path would let the isolation decision and the confinement
        // root diverge (they resolve against different working directories otherwise).
        try
        {
            return Path.GetFullPath(directory);
        }
        catch (Exception)
        {
            return directory;
        }
    }
}
