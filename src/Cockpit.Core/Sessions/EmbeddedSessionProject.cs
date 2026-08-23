using Cockpit.Core.Projects;
using Cockpit.Core.WorkingPaths;
using Cockpit.Core.Worktrees;

namespace Cockpit.Core.Sessions;

// AC-320: which project a session a plugin embeds is working on. A plugin-embedded run (an Autopilot step,
// a workflow) names only a folder, so the host derives the answer from it; without one, a per-project
// contribution to a starting session (AC-165) silently never fires for the autonomous run driving `gh`.
public static class EmbeddedSessionProject
{
    // The project `workingDirectory` works on, or `null` when it belongs to none. Walks back from any worktree
    // to the repository it was cut from before asking — a worktree belongs to no project itself, and asking
    // first would let a broadly-scoped project (e.g. a home directory) claim every isolated run instead.
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
