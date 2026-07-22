using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// The directory an Autopilot run works in (AC-174). The operator's active session's working directory when they have a
/// session open on a repo (the run follows what they are looking at), else the cockpit's own working directory — the
/// folder it was launched in. Without the fallback a run started with no session in view (CEO-first from the side menu)
/// has no directory at all, so its steps are refused for "no git repository" before they can do anything; the fallback
/// gives such a run a repo to isolate in. A directory that is still not a git repository is refused fail-closed
/// downstream (safety over function) — this only widens where a repo is looked for, it never runs an unisolated step.
/// </summary>
internal static class AutopilotWorkingDirectory
{
    public static string Resolve(IWorkspaceContext context)
    {
        var active = context.Sessions.ActiveSessionWorkingDirectory;
        return string.IsNullOrWhiteSpace(active) ? Directory.GetCurrentDirectory() : active;
    }
}
