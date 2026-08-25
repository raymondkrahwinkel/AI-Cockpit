namespace Cockpit.Infrastructure.Worktrees;

// Comparing paths the way the filesystem underneath does. Windows and macOS usually treat `Config.json` and
// `config.json` as one file; Linux usually does not. Getting that wrong in either direction is how a check
// that looks careful reports "nothing in the way" about a file it is standing on.
internal static class GitPaths
{
    // What the platform normally does. "Usually" is the caveat: a repository can sit on a mount that disagrees
    // with its host, so anything comparing paths git handed us asks git instead, via `ComparisonForAsync`.
    public static readonly StringComparison PlatformComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    // How this repository's filesystem compares names, from `core.ignorecase` — which git wrote after probing
    // the filesystem itself when the repository was created, rather than inferring it from the operating system.
    // Falls back to `PlatformComparison` when the setting is absent or unreadable.
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

    // The set comparer matching `comparison`, for indexing paths rather than comparing them one by one.
    public static StringComparer ComparerFor(StringComparison comparison) =>
        comparison == StringComparison.OrdinalIgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    // Every folder on the way to `path`, outermost last — `a/b/c.txt` gives `a/b` and
    // `a`. Git writes forward slashes on every platform, so this needs no separator handling of its own.
    public static IEnumerable<string> ParentsOf(string path)
    {
        for (var slash = path.LastIndexOf('/'); slash > 0; slash = path.LastIndexOf('/', slash - 1))
        {
            yield return path[..slash];
        }
    }
}
