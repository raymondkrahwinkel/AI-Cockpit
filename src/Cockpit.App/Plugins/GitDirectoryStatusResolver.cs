using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.App.Plugins;

// Resolves a directory's `GitDirectoryStatus` fail-closed for the Autopilot isolation gate (AC-174).
// A run drops worktree isolation only on a positive `GitDirectoryStatus.NotARepository`, so this must
// never report that from a git probe merely failing: git refusing to read a real repository (dubious ownership on a
// bind-mount or a differently-owned checkout, a permission or lock error, a repository with no commit yet) returns no
// repository info, but the folder *is* under git and running unisolated there would write in the real checkout.
//
// So "not a repository" is decided from the filesystem, not from the probe: only when there is no `.git` in the
// directory or any ancestor — locale- and ownership-independent, no git process involved — is it definitively not a
// repository. A git-confirmed repository is `GitDirectoryStatus.Repository`; anything else (a `.git`
// exists but git could not confirm it, a missing or unreadable path) is `GitDirectoryStatus.Unknown`,
// which the caller isolates.
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
