using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.App.Plugins;

/// <summary>
/// Resolves a directory's <see cref="GitDirectoryStatus"/> fail-closed for the Autopilot isolation gate (AC-174).
/// A run drops worktree isolation only on a positive <see cref="GitDirectoryStatus.NotARepository"/>, so this must
/// never report that from a git probe merely failing: git refusing to read a real repository (dubious ownership on a
/// bind-mount or a differently-owned checkout, a permission or lock error, a repository with no commit yet) returns no
/// repository info, but the folder <em>is</em> under git and running unisolated there would write in the real checkout.
/// <para>
/// So "not a repository" is decided from the filesystem, not from the probe: only when there is no <c>.git</c> in the
/// directory or any ancestor — locale- and ownership-independent, no git process involved — is it definitively not a
/// repository. A git-confirmed repository is <see cref="GitDirectoryStatus.Repository"/>; anything else (a <c>.git</c>
/// exists but git could not confirm it, a missing or unreadable path) is <see cref="GitDirectoryStatus.Unknown"/>,
/// which the caller isolates.
/// </para>
/// </summary>
internal static class GitDirectoryStatusResolver
{
    /// <param name="gitConfirmedRepository">Whether git positively detected a usable repository here (a non-null <c>DetectRepositoryAsync</c>).</param>
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
