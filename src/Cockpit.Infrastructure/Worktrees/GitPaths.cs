namespace Cockpit.Infrastructure.Worktrees;

/// <summary>
/// Comparing paths the way the filesystem underneath does. Windows and macOS usually treat <c>Config.json</c> and
/// <c>config.json</c> as one file; Linux usually does not. Getting that wrong in either direction is how a check
/// that looks careful reports "nothing in the way" about a file it is standing on.
/// </summary>
internal static class GitPaths
{
    /// <summary>
    /// What the platform normally does — the answer for paths that are not inside a repository, and the fallback for
    /// those that are. "Usually" is the whole caveat: a repository can sit on a mount that disagrees with its host
    /// (a Linux checkout on a CIFS share or a WSL-mounted Windows drive), so anything comparing paths git handed us
    /// asks git instead, through <see cref="ComparisonForAsync"/>.
    /// </summary>
    public static readonly StringComparison PlatformComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// How this repository's filesystem compares names, from <c>core.ignorecase</c> — which git wrote after probing
    /// the filesystem itself when the repository was created, rather than inferring it from the operating system.
    /// Falls back to <see cref="PlatformComparison"/> when the setting is absent or unreadable.
    /// </summary>
    public static async Task<StringComparison> ComparisonForAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        var configured = await GitCli.RunAsync(
            repositoryRoot,
            ["config", "--get", "--type=bool", "core.ignorecase"],
            cancellationToken).ConfigureAwait(false);

        if (configured.ExitCode != 0)
        {
            return PlatformComparison;
        }

        return configured.StandardOutput.Trim().Equals("true", StringComparison.Ordinal)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    }

    /// <summary>The set comparer matching <paramref name="comparison"/>, for indexing paths rather than comparing them one by one.</summary>
    public static StringComparer ComparerFor(StringComparison comparison) =>
        comparison == StringComparison.OrdinalIgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// Every folder on the way to <paramref name="path"/>, outermost last — <c>a/b/c.txt</c> gives <c>a/b</c> and
    /// <c>a</c>. Git writes forward slashes on every platform, so this needs no separator handling of its own.
    /// </summary>
    public static IEnumerable<string> ParentsOf(string path)
    {
        for (var slash = path.LastIndexOf('/'); slash > 0; slash = path.LastIndexOf('/', slash - 1))
        {
            yield return path[..slash];
        }
    }
}
