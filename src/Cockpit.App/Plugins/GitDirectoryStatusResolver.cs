using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.App.Plugins;

// AC-174: resolves `GitDirectoryStatus` fail-closed for the Autopilot isolation gate — a git probe
// merely failing must never read as `NotARepository`, since that drops worktree isolation. "Not a
// repository" is decided from the filesystem (presence of `.git` in an ancestor), not from the probe.
internal static class GitDirectoryStatusResolver
{
    // `gitConfirmedRepository`: Whether git positively detected a usable repository here (a non-null `DetectRepositoryAsync`).
    public static GitDirectoryStatus Resolve(string directory, bool gitConfirmedRepository)
    {
        if (gitConfirmedRepository)
        {
            return GitDirectoryStatus.Repository;
        }

        if (string.IsNullOrWhiteSpace(directory))
        {
            return GitDirectoryStatus.Unknown;
        }

        return _HasNoGitInTree(directory) ? GitDirectoryStatus.NotARepository : GitDirectoryStatus.Unknown;
    }

    // True only when there is provably no .git in the directory or any ancestor — the one case that licenses running
    // unisolated. An unusable or missing path returns false (Unknown, isolate), never a licence to run free.
    private static bool _HasNoGitInTree(string directory)
    {
        DirectoryInfo start;
        try
        {
            var info = new DirectoryInfo(Path.GetFullPath(directory));
            // Resolve a symlinked directory to its real target before the walk: git walks the physical tree, so a
            // symlink pointing into a repository must not read here as "no .git" (which would drop isolation for the
            // real checkout the symlink leads to). A non-symlink resolves to null and keeps the directory itself.
            start = info.ResolveLinkTarget(returnFinalTarget: true) as DirectoryInfo ?? info;
        }
        catch (Exception)
        {
            return false;
        }

        if (!start.Exists)
        {
            return false;
        }

        for (DirectoryInfo? current = start; current is not null; current = current.Parent)
        {
            var gitPath = Path.Combine(current.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
            {
                return false;
            }
        }

        return true;
    }
}
