namespace Cockpit.Core.Projects;

// AC-1013: Whether a ProjectResource.Reference likely names credential material (AC-612) — AC-605's portable
// `~/` made a shared `~/.ssh/id_rsa` row leak whichever operator's key resolves there if SendsContent is ticked.
// A heuristic, not a security boundary — narrow list, no content/symlink inspection; see ticket history for the false-positive-costs-three-times reasoning.
public static class ProjectResourceSecretPathHeuristic
{
    // Home-relative (forward-slash) directories whose entire contents this heuristic treats as sensitive — the
    // directory reference itself included, since pointing a row straight at one of these folders names it exactly
    // as plainly as pointing at a file inside it would.
    private static readonly string[] _SensitiveDirectories =
    [
        ".ssh",
        ".gnupg",
        ".aws",
        ".kube",
        ".config/gh",
    ];

    // Home-relative (forward-slash) exact files, outside the directories above, that are themselves credential material.
    private static readonly string[] _SensitiveFiles =
    [
        ".docker/config.json",
        ".netrc",
        ".npmrc",
        ".pypirc",
    ];

    // AC-1013: Whether `reference` resolves to likely credential material. False for anything this class
    // cannot fairly judge (blank, not home-anchored/fully-qualified, or path APIs throwing) — same fail-open
    // rule ProjectResourcePathPortability follows: say nothing over one bad row rather than guess wrong.
    public static bool IsLikelySecretPath(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return false;
        }

        try
        {
            if (!ProjectResourcePathPortability.IsHomeAnchored(reference) && !Path.IsPathFullyQualified(reference))
            {
                return false;
            }

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var full = Path.GetFullPath(ProjectResourcePathPortability.ResolveHomeAnchor(reference));

            if (!_IsUnderOrEqual(home, full))
            {
                return false;
            }

            var relative = Path.GetRelativePath(home, full).Replace('\\', '/');
            var slash = relative.LastIndexOf('/');
            var fileName = slash < 0 ? relative : relative[(slash + 1)..];

            // AC-1013: A public key (".pub") is meant to be shared — never flagged, even inside an otherwise
            // sensitive directory. Checked universally, before every rule below, not only against id_*.
            if (fileName.EndsWith(".pub", _Comparison))
            {
                return false;
            }

            return _SensitiveDirectories.Any(dir => relative.Equals(dir, _Comparison) || relative.StartsWith(dir + "/", _Comparison))
                || _SensitiveFiles.Any(file => relative.Equals(file, _Comparison))
                || _LooksLikeKeyFileName(fileName);
        }
        catch
        {
            return false;
        }
    }

    // Filename shapes that name a private key wherever they sit under home — not tied to one folder, since an
    // operator can keep a key anywhere (a downloads folder, a backup). `.pub` is excluded before this ever
    // runs (see `IsLikelySecretPath`), so `id_rsa.pub` never reaches here at all.
    private static bool _LooksLikeKeyFileName(string fileName) =>
        fileName.Length > 0 &&
        (fileName.StartsWith("id_", _Comparison)
            || fileName.EndsWith(".pem", _Comparison)
            || fileName.EndsWith(".key", _Comparison)
            || fileName.EndsWith(".p12", _Comparison)
            || fileName.EndsWith(".pfx", _Comparison));

    // Same platform rule ProjectResourcePathPortability._IsUnder already uses: Windows paths are case-insensitive,
    // so ".SSH" and ".ssh" name the same folder there but two different (and on Linux, almost certainly absent)
    // folders on a case-sensitive filesystem.
    private static StringComparison _Comparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static bool _IsUnderOrEqual(string normalizedHome, string fullPath) =>
        fullPath.Equals(normalizedHome, _Comparison)
            || fullPath.StartsWith(normalizedHome + Path.DirectorySeparatorChar, _Comparison);
}
