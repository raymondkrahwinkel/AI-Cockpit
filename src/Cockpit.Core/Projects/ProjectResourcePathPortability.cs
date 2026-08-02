namespace Cockpit.Core.Projects;

// Turns an absolute path the operator just picked for a `ProjectResource` row into something a shared
// project definition can actually carry (AC-485). An absolute path names a place on *this* machine —
// `C:\Users\raymond\...` means nothing once the project definition travels to someone else's — so a path the
// operator picked from inside the project's own `Project.SourceDirectory` is stored relative to it
// instead, the same way a git checkout already makes every file under it portable by construction. A path outside
// that folder has no such anchor to travel with, so it is left exactly as picked — see `ClassifyScope`
// for the other half of that: making that limit visible rather than silent.
//
// Only *picked* paths go through `ToStoredReference` — a path the operator types by hand is
// stored verbatim, whatever shape it has. Guessing at portability for typed text would mean silently rewriting
// something the operator did not ask this to touch; this is deliberately the picker's own step, not a rule applied
// to every keystroke in the box. `SuggestRepoRelativeFix` is the one exception (AC-605 criterion 5): it
// reuses this same method to compute what a hand-typed absolute reference *would* look like stored properly,
// without ever assigning it — only an explicit "make repo-relative" action in the editor writes it back.
//
// AC-485 review (FIX 5): a relative reference this class stores always uses `/` as its separator, never
// `Path.DirectorySeparatorChar` — see `ToStoredReference`'s own remark on why a
// platform-specific separator is exactly the thing a "shared project definition" and a "git checkout" must not
// carry.
//
// AC-485 review (FIX 8) — the same platform asymmetry `Infrastructure.Projects.ProjectResourceProbe`
// already documents for itself (AC-484 review, FIX 7) applies here too, and bites harder: whether
// `Path.IsPathFullyQualified(string)` calls a reference "fully qualified" depends on the OS this
// runtime is on, so `ClassifyScope` never reads a POSIX-shaped reference (`/home/raymond/Notes`)
// as `ProjectResourceScope.Machine` on Windows, nor a `C:\...` reference as one on Linux —
// whichever platform authored a project's resources, the warning that exists to say "this will not travel" simply
// never appears for a colleague on the other one. That is worse here than in the probe: this warning's entire
// purpose is to be seen by someone who might be on a different machine than the one that picked the path, which is
// exactly the case it cannot detect. There is no project-portable notion of "absolute path" for either class to
// fall back on instead, so this is written down rather than "fixed".
//
// AC-605: a `~`-anchored reference is deliberately never checked for whether it stays inside the resolved home
// directory once `..` segments are applied (`~/../../etc/passwd` resolves wherever that lands, however
// far outside home it is) — the same restraint `ToStoredReference` already applies to a relative,
// repo-anchored reference containing its own `..` segments, which this class has never bounds-checked either.
// Classification answers what kind of reference this is, not whether it is a well-behaved one.
public static class ProjectResourcePathPortability
{
    // `pickedPath` as it should be stored: relative to `sourceDirectory` when it
    // lives inside it (including being the folder itself), unchanged otherwise. Both sides are resolved with
    // `Path.GetFullPath(string)` before comparing, so a trailing separator or a relative segment in
    // either one does not defeat the check.
    //
    // AC-485 review (FIX 5): the relative path itself is stored with `/` as its separator, whatever platform
    // picked it — `Path.GetRelativePath(string, string)` answers in this platform's own separator, and
    // the class doc's own comparison to a git checkout is not idle: git stores `/` in a tree entry regardless
    // of the platform that committed it, precisely so a relative path is a definition every platform can carry, not
    // a filename with a backslash in it on whichever machine did not pick it.
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
            // AC-485 review (FIX 7): Path.GetFullPath throws for a path this runtime's own filesystem APIs refuse
            // outright — a NUL byte typed or pasted in, say. That is not this method's failure to raise: see
            // ProjectResourceEntry's own remark that a hand-edited cockpit.json must cost one row, not the whole
            // dialog. Stored exactly as picked, the same as any path outside sourceDirectory.
            return pickedPath;
        }
    }

    // How far `reference` travels (AC-605 criteria 3, 6, 7) — the scope-question that replaces
    // what used to be the single yes/no `IsMachineBound`: a method that only ever answered "machine-bound or
    // not" could not represent "anchored to a home folder, so it travels to everyone the project is shared with,
    // each resolved against their own" as anything but a bare false, indistinguishable from a portable repo-relative
    // reference — which is exactly how the AC-244 classifier and this class ended up disagreeing about a
    // `~/...` reference without either side noticing. (Raymond, AC-605 review round: this doc comment itself
    // used to say "travels with *them*", the same one-person framing the class rewrite below now retires.)
    //
    // Deliberately takes no `sourceDirectory` and mirrors
    // `Cockpit.Plugin.Depot.ProjectDefinition.ProjectResourcePortabilityClassifier.Classify` exactly (AC-605
    // criterion 4) — a stored reference's shape alone says what it is; whether an absolute reference happens to sit
    // inside the current project folder is a different question, answered by `SuggestRepoRelativeFix`.
    // Null only for a blank reference — nothing to classify.
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

    // The repo-relative form `reference` should have been stored as, when it is a fully qualified
    // path that lives inside `sourceDirectory` but never actually went through
    // `ToStoredReference` — hand-typed, or written into a hand-edited `cockpit.json` (AC-605
    // criterion 5). Reuses `ToStoredReference` itself to compute it rather than duplicating the
    // "is it under the folder" check a second time, without ever calling it on the operator's behalf — see this
    // class's own remarks on why only a picked path is rewritten automatically. Null when there is nothing to fix:
    // `reference` is not an absolute path, is already outside `sourceDirectory`
    // (nothing to anchor it to), or malformed the same way `ToStoredReference` already fails open for.
    public static string? SuggestRepoRelativeFix(string? sourceDirectory, string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference) || !Path.IsPathFullyQualified(reference))
        {
            return null;
        }

        var stored = ToStoredReference(sourceDirectory, reference);
        return string.Equals(stored, reference, StringComparison.Ordinal) ? null : stored;
    }

    // Whether `reference` is anchored to the operator's home directory (AC-605) — exactly `~`
    // itself, or anything starting with `~/`. Deliberately narrower than a bare
    // `reference.StartsWith('~')` (what the AC-244 classifier used before this ticket): `~henk/x` is a
    // POSIX shell's "some other user's home" expansion, a shell feature .NET's own path APIs know nothing about —
    // treating it as this operator's home would resolve to the wrong place silently. Only `~` and `~/...`
    // are a supported anchor form (Raymond's decision, AC-605); anything else starting with `~` is left as
    // ordinary text, the same as any other reference this class does not recognise a shape for.
    public static bool IsHomeAnchored(string? reference) =>
        reference is "~" || (reference?.StartsWith("~/", StringComparison.Ordinal) ?? false);

    // `reference` with its `~` anchor resolved to an actual path on this machine (AC-605
    // criterion 1) — the one place that does this; every caller that needs an anchored reference as a real
    // filesystem path (`Infrastructure.Projects.ProjectResourceProbe`,
    // `Infrastructure.Projects.ProjectInstructionContentReader` actually opening a row's file, the
    // project editor's own folder picker seeding itself from a typed Memory-folder reference) goes through here
    // rather than expanding `~` itself — a caller that skips this and hands a raw `"~/..."` string
    // straight to a filesystem API gets an exception on every platform, not a resolved path (measured: exactly
    // what `Infrastructure.Projects.ProjectInstructionContentReader` did until AC-605's own review
    // round caught it — this doc comment asserted the reader "goes through here" before it actually did).
    // `Environment.SpecialFolder.UserProfile`
    // rather than the `HOME`/`USERPROFILE` environment variable directly: it already resolves to
    // `$HOME` on Linux/macOS and the profile folder on Windows, so this needs no platform branch of its own.
    //
    // Returns `reference` unchanged when it is not home-anchored (see `IsHomeAnchored`)
    // — including `~henk/x`, which this deliberately never expands (see `IsHomeAnchored`'s own
    // remarks) — or when resolving it throws, the same fail-open rule `ToStoredReference` already
    // follows for a reference this runtime's filesystem APIs refuse outright (a NUL byte, say).
    //
    // Does not special-case a suffix that climbs back out of the home directory (`~/../../etc/passwd`) — see
    // this class's own remarks on why a bounds check is not this method's job.
    public static string ResolveHomeAnchor(string reference)
    {
        if (!IsHomeAnchored(reference))
        {
            return reference;
        }

        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            // reference is either exactly "~" or starts with "~/" (IsHomeAnchored already confirmed it); either
            // way, everything from index 1 on is the suffix to resolve against home, minus whatever further
            // leading separators it opens with ("~//x" must not be read as an absolute path that replaces home
            // outright the way Path.Combine(home, "/x") would read it).
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
