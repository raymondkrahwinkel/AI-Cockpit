namespace Cockpit.Plugin.GitStatus;

/// <summary>
/// Finds the HEAD file that governs the branch a working directory is on. It is not reliably
/// <c>&lt;dir&gt;/.git/HEAD</c>: the session may be working in a subdirectory of the repository, or in a linked
/// worktree, each of which git points at a different git directory. Asking git itself
/// (<c>rev-parse --absolute-git-dir</c>) is the one way that stays correct across all of them — the header
/// control watches the returned file so the branch badge follows a checkout made outside the session.
/// </summary>
internal static class GitHeadLocator
{
    public static async Task<string?> ResolveHeadFileAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        try
        {
            var gitDirectory = await GitCommand.RunAsync(workingDirectory, ["rev-parse", "--absolute-git-dir"], cancellationToken);
            if (gitDirectory.Length == 0)
            {
                return null;
            }

            var headFile = Path.Combine(gitDirectory, "HEAD");
            return File.Exists(headFile) ? headFile : null;
        }
        catch (InvalidOperationException)
        {
            // Not a repository, or git is missing — either way there is no HEAD to watch. The header already
            // hides itself in that case; here it just means no watcher gets wired.
            return null;
        }
    }
}
