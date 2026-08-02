namespace Cockpit.Core.Projects;

// Whether a `ProjectResource.Reference` likely names a place that holds credential material — an SSH
// private key, cloud credentials, a token file (AC-612). Exists because AC-605 made `~/` a portable
// reference: `~/.ssh/id_rsa` now travels in a shared `.cockpit/project.json` and resolves, on every
// machine that opens it, to *that* operator's own key — and a row with `ProjectResource.SendsContent`
// ticked would read that file's bytes straight into a session prompt (see `ProjectResource.SendsContent`'s
// own doc comment for where that is actually stopped).
//
// *This is a heuristic, not a security boundary.* A hand-picked, deliberately narrow list of well-known
// secret locations under the operator's home directory — nothing here inspects file contents, follows a symlink
// to somewhere this list would have recognised, or catches a credential kept somewhere this list does not name.
// Say so wherever this class's answer reaches an operator; a heuristic presented as a guarantee is worse than no
// heuristic at all — the same "the sentence above the code is the defect" shape this epic has already produced
// three times, applied here in advance rather than found in review.
//
// AC-612 (Raymond): recognising a row here closes three doors at once — it is reported, its content can never be
// sent, and it never reaches a shared definition — so a false positive costs three times what an ordinary one
// would. The list stays narrow and defensible on purpose: missing a real secrets folder leaves a row behaving
// exactly as it did before this ticket (no worse than today), while wrongly flagging an ordinary file costs three
// things an operator did nothing to deserve. When in doubt about a pattern, it stays out.
//
// Scoped to a reference `ProjectResourcePathPortability.IsHomeAnchored` already recognises, or one
// `Path.IsPathFullyQualified(string)` already calls fully qualified — nothing else: a plugin-scheme
// reference is not a filesystem path at all, and a
// repo-relative reference (`docs/notes.md`) is not the risk AC-605 introduced (a secret committed straight
// into a repo is an older, different problem this ticket does not reach). Works on the *resolved* form
// (`ProjectResourcePathPortability.ResolveHomeAnchor`), so an absolute path naming the same location
// as an anchor form is recognised identically (AC-612 criterion, mirroring `Infrastructure.Projects.ProjectResourceProbe`'s
// own reasoning for doing the same).
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

    // Whether `reference` resolves to a location this heuristic recognises as likely credential
    // material. False for anything this class cannot fairly judge — blank, not home-anchored or fully qualified
    // (see the class remarks on scope), or a reference this runtime's own path APIs refuse outright — the same
    // fail-open rule every sibling method in `ProjectResourcePathPortability` already follows: better
    // to say nothing than to guess wrong in either direction over one bad row.
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

            // A public key (or anything else ending ".pub") is meant to be shared by definition — never flagged,
            // even sitting inside a directory this heuristic otherwise treats as sensitive wholesale. Checked
            // before every other rule below, universally, rather than only against the id_* pattern: the exception
            // is about what the file plainly is, not about which rule would otherwise have caught it.
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
