using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.Plugin.Autopilot;

// The directory an Autopilot run works in (AC-174): the operator's chosen folder at approval, else the active
// session's working directory, else the cockpit's own launch directory. Whether it is isolated per step depends
// on whether it is a git repository — that decision is made downstream, this only resolves where it is.
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
