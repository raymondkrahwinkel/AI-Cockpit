namespace Cockpit.Core.Projects;

// AC-1013: Turns an operator-picked absolute path for a ProjectResource row into something a shared project
// definition can carry (AC-485) — relative to SourceDirectory when inside it, unchanged otherwise; only picked
// paths are rewritten. Known, deliberately unfixed limits: FIX 8's platform-dependent travel warning, AC-605's unbounded `~/..`-anchor — see ticket history.
public static class ProjectResourcePathPortability
{
    // AC-1013: `pickedPath` as it should be stored — relative to `sourceDirectory` when inside it, unchanged
    // otherwise (both sides GetFullPath-resolved first). Always stored with `/` as separator (FIX 5), even on
    // Windows, matching how git itself stores tree entries regardless of the committing platform.
    public static string ToStoredReference(string? sourceDirectory, string pickedPath)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory) || !Path.IsPathFullyQualified(pickedPath))
        {
            return pickedPath;
        }

        try
        {
            var root = _NormalizedRoot(sourceDirectory);
            var full = Path.GetFullPath(pickedPath);

            return _IsUnder(root, full) ? Path.GetRelativePath(root, full).Replace('\\', '/') : pickedPath;
        }
        catch
        {
            // AC-1013: FIX 7 — GetFullPath throws on a path the runtime's own APIs refuse (e.g. a NUL byte); a
            // hand-edited cockpit.json must cost one row, not the whole dialog, so this fails open, unchanged.
            return pickedPath;
        }
    }

    // AC-1013: How far `reference` travels (AC-605 criteria 3,6,7), replacing the old yes/no IsMachineBound
    // which could not represent "home-anchored, resolved per-operator" and led it to disagree with the AC-244
    // classifier about `~/...`. Takes no sourceDirectory — shape alone decides; null only for a blank reference.
    public static ProjectResourceScope? ClassifyScope(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        if (ProjectMemoryRef.TryParse(reference, out _, out _))
        {
            return ProjectResourceScope.Instance;
        }

        if (IsHomeAnchored(reference))
        {
            return ProjectResourceScope.Home;
        }

        try
        {
            return Path.IsPathFullyQualified(reference) ? ProjectResourceScope.Machine : ProjectResourceScope.Repo;
        }
        catch
        {
            // AC-485 review (FIX 7)'s malformed-reference case, mirrored here: better to say nothing about a
            // reference this method cannot fairly judge than to crash the editor over one bad row.
            return null;
        }
    }

    // AC-1013: The repo-relative form `reference` should have been stored as (AC-605 criterion 5) — for a
    // hand-typed or hand-edited absolute path never rewritten by ToStoredReference. Reuses ToStoredReference
    // to compute it without ever assigning it; null when there is nothing to fix.
    public static string? SuggestRepoRelativeFix(string? sourceDirectory, string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference) || !Path.IsPathFullyQualified(reference))
        {
            return null;
        }

        var stored = ToStoredReference(sourceDirectory, reference);
        return string.Equals(stored, reference, StringComparison.Ordinal) ? null : stored;
    }

    // AC-1013: Whether `reference` is anchored to the operator's home (AC-605) — exactly `~` or `~/...` only,
    // deliberately narrower than the old bare StartsWith('~'): `~henk/x` is a POSIX "other user's home" shell
    // expansion .NET path APIs don't understand, so treating it as this operator's home would resolve silently wrong.
    public static bool IsHomeAnchored(string? reference) =>
        reference is "~" || (reference?.StartsWith("~/", StringComparison.Ordinal) ?? false);

    // AC-1013: `reference` with `~` resolved to a real path (AC-605 criterion 1) — the one place every caller
    // needing a real filesystem path must go through (a raw "~/..." throws on any filesystem API otherwise).
    // Unchanged for non-home-anchored or on failure; never bounds-checks a `..`-climbing suffix, see class remarks.
    public static string ResolveHomeAnchor(string reference)
    {
        if (!IsHomeAnchored(reference))
        {
            return reference;
        }

        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            // AC-1013: index 1+ is the suffix to resolve against home; strip its own leading separators so
            // "~//x" isn't read as an absolute path that replaces home the way Path.Combine(home, "/x") would.
            var suffix = reference[1..].TrimStart('/', '\\');

            return suffix.Length == 0 ? home : Path.GetFullPath(Path.Combine(home, suffix));
        }
        catch
        {
            return reference;
        }
    }

    private static string _NormalizedRoot(string sourceDirectory) =>
        Path.GetFullPath(sourceDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool _IsUnder(string normalizedRoot, string fullPath)
    {
        // Windows paths are case-insensitive; comparing case-sensitively there would call a path "outside" the
        // folder it is plainly inside just because a picker or a typed drive letter disagreed on casing.
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        return fullPath.Equals(normalizedRoot, comparison)
            || fullPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison);
    }
}
