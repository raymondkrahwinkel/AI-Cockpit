namespace Cockpit.Plugin.Depot.ProjectDefinition;

/// <summary>
/// Whether a resource reference likely names a place that holds credential material (AC-612) — mirrors
/// <c>Cockpit.Core.Projects.ProjectResourceSecretPathHeuristic</c> exactly, the same "cannot reference Cockpit.Core"
/// constraint <see cref="ProjectResourcePortabilityClassifier"/> already documents for itself (AC-244/AC-605). Kept
/// in sync by <c>ProjectResourceSecretPathParityTests</c>, which runs the same table against both copies and goes
/// red on either side the moment the two disagree.
/// <para>
/// <b>This is a heuristic, not a security boundary.</b> A hand-picked, deliberately narrow list of well-known
/// secret locations under the operator's home directory — nothing here inspects file contents, follows a symlink,
/// or catches a credential kept somewhere this list does not name. Say so wherever this class's answer reaches an
/// operator; a heuristic presented as a guarantee is worse than no heuristic at all.
/// </para>
/// <para>
/// AC-612 (Raymond): a row this recognises is dropped from the written definition outright (see
/// <see cref="CockpitProjectResourceEntry.Create"/>) — a false positive here costs three things at once (reported,
/// content withheld, dropped from the shared definition), so the list stays narrow and defensible on purpose:
/// missing a real secrets folder leaves a row behaving exactly as it did before this ticket, while wrongly flagging
/// an ordinary file costs three things an operator did nothing to deserve.
/// </para>
/// </summary>
public static class ProjectResourceSecretPathHeuristic
{
    private static readonly string[] _SensitiveDirectories =
    [
        ".ssh",
        ".gnupg",
        ".aws",
        ".kube",
        ".config/gh",
    ];

    private static readonly string[] _SensitiveFiles =
    [
        ".docker/config.json",
        ".netrc",
        ".npmrc",
        ".pypirc",
    ];

    /// <summary>
    /// Whether <paramref name="reference"/> resolves to a location this heuristic recognises as likely credential
    /// material. False for anything this class cannot fairly judge — blank, not home-anchored or fully qualified
    /// (a plugin-scheme or repo-relative reference is out of scope — see the class remarks), or a reference this
    /// runtime's own path APIs refuse outright.
    /// </summary>
    public static bool IsLikelySecretPath(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return false;
        }

        try
        {
            if (!_IsHomeAnchored(reference) && !Path.IsPathFullyQualified(reference))
            {
                return false;
            }

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var full = Path.GetFullPath(_ResolveHomeAnchor(reference));

            if (!_IsUnderOrEqual(home, full))
            {
                return false;
            }

            var relative = Path.GetRelativePath(home, full).Replace('\\', '/');
            var slash = relative.LastIndexOf('/');
            var fileName = slash < 0 ? relative : relative[(slash + 1)..];

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

    private static bool _LooksLikeKeyFileName(string fileName) =>
        fileName.Length > 0 &&
        (fileName.StartsWith("id_", _Comparison)
            || fileName.EndsWith(".pem", _Comparison)
            || fileName.EndsWith(".key", _Comparison)
            || fileName.EndsWith(".p12", _Comparison)
            || fileName.EndsWith(".pfx", _Comparison));

    private static StringComparison _Comparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static bool _IsUnderOrEqual(string normalizedHome, string fullPath) =>
        fullPath.Equals(normalizedHome, _Comparison)
            || fullPath.StartsWith(normalizedHome + Path.DirectorySeparatorChar, _Comparison);

    // Mirrors Cockpit.Core.Projects.ProjectResourcePathPortability.IsHomeAnchored exactly (same reasoning as
    // ProjectResourcePortabilityClassifier's own copy) — only "~" itself or "~/..." is a supported anchor form.
    private static bool _IsHomeAnchored(string reference) =>
        reference == "~" || reference.StartsWith("~/", StringComparison.Ordinal);

    // Mirrors Cockpit.Core.Projects.ProjectResourcePathPortability.ResolveHomeAnchor exactly.
    private static string _ResolveHomeAnchor(string reference)
    {
        if (!_IsHomeAnchored(reference))
        {
            return reference;
        }

        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var suffix = reference[1..].TrimStart('/', '\\');

            return suffix.Length == 0 ? home : Path.GetFullPath(Path.Combine(home, suffix));
        }
        catch
        {
            return reference;
        }
    }
}
