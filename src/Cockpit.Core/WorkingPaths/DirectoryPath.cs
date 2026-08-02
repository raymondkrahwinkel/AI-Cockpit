namespace Cockpit.Core.WorkingPaths;

// Comparing folders the way the platform does. Two spellings of one folder — a trailing separator, a relative
// segment, a different case where the file system does not care — are the same folder, and code that decides
// what a session works on has to agree with the file system about that.
public static class DirectoryPath
{
    // How folder names compare here: case-insensitively on Windows and macOS, exactly on Linux — the same rule the
    // worktree engine applies, so a path means one thing across the app rather than one thing per caller.
    public static readonly StringComparison Comparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    // `Comparison` as a comparer, for a set or dictionary keyed by folder.
    public static readonly StringComparer Comparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    // `path` as one absolute, separator-normalised folder name without a trailing separator, or
    // `null` when it names no folder — blank, or a path the platform itself rejects. Null rather
    // than a throw: this runs on the way to starting a session, and an unusable path is an answer, not a failure.
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Whether `path` is `folder` itself or something inside it. The containment
    // test is on a separator boundary, so `/repo-two` is not inside `/repo`.
    public static bool IsWithin(string? path, string? folder)
    {
        if (Normalize(path) is not { } target || Normalize(folder) is not { } root)
        {
            return false;
        }

        if (string.Equals(root, target, Comparison))
        {
            return true;
        }

        // Compared against the folder plus its separator, so /repo-two is not inside /repo. A root folder ("/", "C:\")
        // already ends in one — Normalize leaves those alone, because a root without its separator is not a path — so
        // adding a second would mean nothing is ever inside a project scoped to a drive.
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return target.StartsWith(prefix, Comparison);
    }
}
