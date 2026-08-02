namespace Cockpit.Core.Projects;

// Turns an absolute path the operator just picked for a `ProjectResource` row into something a shared
// project definition can actually carry (AC-485). An absolute path names a place on *this* machine —
// `C:\Users\raymond\...` means nothing once the project definition travels to someone else's — so a path the
// operator picked from inside the project's own `Project.SourceDirectory` is stored relative to it
// instead, the same way a git checkout already makes every file under it portable by construction. A path outside
// that folder has no such anchor to travel with, so it is left exactly as picked — see `IsMachineBound`
// for the other half of that: making that limit visible rather than silent.
//
// Only *picked* paths go through `ToStoredReference` — a path the operator types by hand is
// stored verbatim, whatever shape it has. Guessing at portability for typed text would mean silently rewriting
// something the operator did not ask this to touch; this is deliberately the picker's own step, not a rule applied
// to every keystroke in the box.
//
// AC-485 review (FIX 5): a relative reference this class stores always uses `/` as its separator, never
// `Path.DirectorySeparatorChar` — see `ToStoredReference`'s own remark on why a
// platform-specific separator is exactly the thing a "shared project definition" and a "git checkout" must not
// carry.
//
// AC-485 review (FIX 8) — the same platform asymmetry `Infrastructure.Projects.ProjectResourceProbe`
// already documents for itself (AC-484 review, FIX 7) applies here too, and bites harder: whether
// `Path.IsPathFullyQualified(string)` calls a reference "fully qualified" depends on the OS this
// runtime is on, so `IsMachineBound` never fires for a POSIX-shaped reference (`/home/raymond/Notes`) on
// Windows, nor for a `C:\...` reference on Linux — whichever platform authored a project's resources, the
// warning that exists to say "this will not travel" simply never appears for a colleague on the other one. That is
// worse here than in the probe: this warning's entire purpose is to be seen by someone who might be on a different
// machine than the one that picked the path, which is exactly the case it cannot detect. There is no
// project-portable notion of "absolute path" for either class to fall back on instead, so this is written down
// rather than "fixed".
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

    // Whether `reference` is a fully qualified path that does not live inside
    // `sourceDirectory` — a reference that names a location only this machine has, worth
    // surfacing in the editor before it becomes a session's unexplained "could not find it".
    //
    // False for a relative path (already portable, or at least not this method's to judge — see
    // `ToStoredReference`'s own remark on typed text), a `&lt;scheme&gt;:&lt;value&gt;` reference
    // (a plugin's own identifier, not a path at all — the same check `Infrastructure.Projects.ProjectResourceProbe`
    // makes first, for the same reason), or one already inside the project's own folder.
    public static bool IsMachineBound(string? sourceDirectory, string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference) || ProjectMemoryRef.TryParse(reference, out _, out _))
        {
            return false;
        }

        if (!Path.IsPathFullyQualified(reference))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(sourceDirectory))
        {
            return true;
        }

        try
        {
            return !_IsUnder(_NormalizedRoot(sourceDirectory), Path.GetFullPath(reference));
        }
        catch
        {
            // AC-485 review (FIX 7): the same malformed-reference case ToStoredReference guards against (see its
            // own remark) — better to say nothing about a reference this method cannot fairly judge than to flag it
            // machine-bound (or crash the editor) over one bad row, mirroring ProjectResourceProbe's own rule for
            // exactly this class of input.
            return false;
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
