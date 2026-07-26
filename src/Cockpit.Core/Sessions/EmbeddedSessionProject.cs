using Cockpit.Core.Projects;
using Cockpit.Core.WorkingPaths;
using Cockpit.Core.Worktrees;

namespace Cockpit.Core.Sessions;

/// <summary>
/// Which project a session a plugin embeds is working on (AC-320). The New-session routes are handed a project by
/// the operator; a plugin that embeds a session — an Autopilot step, a workflow run — names a folder and nothing
/// else, so the host answers the question from that folder.
/// <para>
/// Without an answer here every embedded session belongs to no project, and everything that hangs off one stays
/// silent exactly where it is worth the most: a plugin's per-project contribution to a starting session (AC-165)
/// does not fire for the autonomous run that is out there driving <c>gh</c> on its own.
/// </para>
/// </summary>
public static class EmbeddedSessionProject
{
    /// <summary>
    /// The project <paramref name="workingDirectory"/> works on, or <see langword="null"/> when it belongs to none.
    /// </summary>
    /// <param name="projects">Every configured project — a project claims the folder it owns.</param>
    /// <param name="worktrees">The worktrees the host has made, so a run pointed at one is placed on what it was cut from.</param>
    /// <param name="workingDirectory">The folder the run was asked to work in, as given — not the isolated one the host derives from it.</param>
    /// <remarks>
    /// The directory as requested, because the worktree a run is isolated into belongs to no project while the
    /// repository it was cut from does. A run pointed <em>straight</em> at a worktree — Autopilot's validating CEO
    /// reads the run's accumulated work there rather than in the checkout — is placed on that repository's project
    /// too, so two sessions of one run never disagree about which project they are on.
    /// <para>
    /// A folder the host made a worktree at is stepped back from <em>before</em> any project is asked about it, and
    /// that order matters: worktrees live under the cockpit's own state folder, so a project scoped broadly enough to
    /// contain that folder — a home directory, say — would otherwise claim every isolated run in the cockpit and hand
    /// it the wrong project's environment. A worktree is something the host cut from a repository, never a folder a
    /// project owns, so where it came from is the only honest answer.
    /// </para>
    /// <para>
    /// The step back is a walk rather than a single hop, because a worktree can be cut from a worktree: a run started
    /// from a session that is itself isolated records its parent as the repository (<c>git rev-parse --show-toplevel</c>
    /// inside a linked worktree answers with that worktree, not the checkout it belongs to). The walk ends at the
    /// first folder that is no worktree the host knows, or when it comes back somewhere it has already been — a
    /// registry that points at itself costs the answer, never the session.
    /// </para>
    /// </remarks>
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
