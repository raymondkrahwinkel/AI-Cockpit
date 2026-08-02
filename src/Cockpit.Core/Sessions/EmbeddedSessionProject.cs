using Cockpit.Core.Projects;
using Cockpit.Core.WorkingPaths;
using Cockpit.Core.Worktrees;

namespace Cockpit.Core.Sessions;

// Which project a session a plugin embeds is working on (AC-320). The New-session routes are handed a project by
// the operator; a plugin that embeds a session — an Autopilot step, a workflow run — names a folder and nothing
// else, so the host answers the question from that folder.
//
// Without an answer here every embedded session belongs to no project, and everything that hangs off one stays
// silent exactly where it is worth the most: a plugin's per-project contribution to a starting session (AC-165)
// does not fire for the autonomous run that is out there driving `gh` on its own.
public static class EmbeddedSessionProject
{
    // The project `workingDirectory` works on, or `null` when it belongs to none.
    //
    // `projects`: Every configured project — a project claims the folder it owns.
    // `worktrees`: The worktrees the host has made, so a run pointed at one is placed on what it was cut from.
    // `workingDirectory`: The folder the run was asked to work in, as given — not the isolated one the host derives from it.
    // The directory as requested, because the worktree a run is isolated into belongs to no project while the
    // repository it was cut from does. A run pointed *straight* at a worktree — Autopilot's validating CEO
    // reads the run's accumulated work there rather than in the checkout — is placed on that repository's project
    // too, so two sessions of one run never disagree about which project they are on.
    //
    // A folder the host made a worktree at is stepped back from *before* any project is asked about it, and
    // that order matters: worktrees live under the cockpit's own state folder, so a project scoped broadly enough to
    // contain that folder — a home directory, say — would otherwise claim every isolated run in the cockpit and hand
    // it the wrong project's environment. A worktree is something the host cut from a repository, never a folder a
    // project owns, so where it came from is the only honest answer.
    //
    // The step back is a walk rather than a single hop, because a worktree can be cut from a worktree: a run started
    // from a session that is itself isolated records its parent as the repository (`git rev-parse --show-toplevel`
    // inside a linked worktree answers with that worktree, not the checkout it belongs to). The walk ends at the
    // first folder that is no worktree the host knows, or when it comes back somewhere it has already been — a
    // registry that points at itself costs the answer, never the session.
    public static Project? Resolve(
        IEnumerable<Project> projects,
        IEnumerable<WorktreeRecord> worktrees,
        string? workingDirectory)
    {
        var registered = worktrees as IReadOnlyCollection<WorktreeRecord> ?? [.. worktrees];
        var visited = new HashSet<string>(DirectoryPath.Comparer);

        var directory = workingDirectory;
        while (DirectoryPath.Normalize(directory) is { } folder && visited.Add(folder))
        {
            if (WorktreeLookup.At(registered, folder) is not { } worktree)
            {
                return ProjectDirectoryMatch.For(projects, folder);
            }

            directory = worktree.RepositoryRoot;
        }

        return null;
    }
}
