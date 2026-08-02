namespace Cockpit.Core.Projects;

/// <summary>
/// Turns an absolute path the operator just picked for a <see cref="ProjectResource"/> row into something a shared
/// project definition can actually carry (AC-485). An absolute path names a place on <em>this</em> machine —
/// <c>C:\Users\raymond\...</c> means nothing once the project definition travels to someone else's — so a path the
/// operator picked from inside the project's own <see cref="Project.SourceDirectory"/> is stored relative to it
/// instead, the same way a git checkout already makes every file under it portable by construction. A path outside
/// that folder has no such anchor to travel with, so it is left exactly as picked — see <see cref="ClassifyScope"/>
/// for the other half of that: making that limit visible rather than silent.
/// <para>
/// Only <em>picked</em> paths go through <see cref="ToStoredReference"/> — a path the operator types by hand is
/// stored verbatim, whatever shape it has. Guessing at portability for typed text would mean silently rewriting
/// something the operator did not ask this to touch; this is deliberately the picker's own step, not a rule applied
/// to every keystroke in the box. <see cref="SuggestRepoRelativeFix"/> is the one exception (AC-605 criterion 5): it
/// reuses this same method to compute what a hand-typed absolute reference <em>would</em> look like stored properly,
/// without ever assigning it — only an explicit "make repo-relative" action in the editor writes it back.
/// </para>
/// <para>
/// AC-485 review (FIX 5): a relative reference this class stores always uses <c>/</c> as its separator, never
/// <see cref="Path.DirectorySeparatorChar"/> — see <see cref="ToStoredReference"/>'s own remark on why a
/// platform-specific separator is exactly the thing a "shared project definition" and a "git checkout" must not
/// carry.
/// </para>
/// <para>
/// AC-485 review (FIX 8) — the same platform asymmetry <see cref="Infrastructure.Projects.ProjectResourceProbe"/>
/// already documents for itself (AC-484 review, FIX 7) applies here too, and bites harder: whether
/// <see cref="Path.IsPathFullyQualified(string)"/> calls a reference "fully qualified" depends on the OS this
/// runtime is on, so <see cref="ClassifyScope"/> never reads a POSIX-shaped reference (<c>/home/raymond/Notes</c>)
/// as <see cref="ProjectResourceScope.Machine"/> on Windows, nor a <c>C:\...</c> reference as one on Linux —
/// whichever platform authored a project's resources, the warning that exists to say "this will not travel" simply
/// never appears for a colleague on the other one. That is worse here than in the probe: this warning's entire
/// purpose is to be seen by someone who might be on a different machine than the one that picked the path, which is
/// exactly the case it cannot detect. There is no project-portable notion of "absolute path" for either class to
/// fall back on instead, so this is written down rather than "fixed".
/// </para>
/// <para>
/// AC-605: a <c>~</c>-anchored reference is deliberately never checked for whether it stays inside the resolved home
/// directory once <c>..</c> segments are applied (<c>~/../../etc/passwd</c> resolves wherever that lands, however
/// far outside home it is) — the same restraint <see cref="ToStoredReference"/> already applies to a relative,
/// repo-anchored reference containing its own <c>..</c> segments, which this class has never bounds-checked either.
/// Classification answers what kind of reference this is, not whether it is a well-behaved one.
/// </para>
/// </summary>
public static class ProjectResourcePathPortability
{
    /// <summary>
    /// <paramref name="pickedPath"/> as it should be stored: relative to <paramref name="sourceDirectory"/> when it
    /// lives inside it (including being the folder itself), unchanged otherwise. Both sides are resolved with
    /// <see cref="Path.GetFullPath(string)"/> before comparing, so a trailing separator or a relative segment in
    /// either one does not defeat the check.
    /// <para>
    /// AC-485 review (FIX 5): the relative path itself is stored with <c>/</c> as its separator, whatever platform
    /// picked it — <see cref="Path.GetRelativePath(string, string)"/> answers in this platform's own separator, and
    /// the class doc's own comparison to a git checkout is not idle: git stores <c>/</c> in a tree entry regardless
    /// of the platform that committed it, precisely so a relative path is a definition every platform can carry, not
    /// a filename with a backslash in it on whichever machine did not pick it.
    /// </para>
    /// </summary>
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

    /// <summary>
    /// How far <paramref name="reference"/> travels (AC-605 criteria 3, 6, 7) — the scope-question that replaces
    /// what used to be the single yes/no <c>IsMachineBound</c>: a method that only ever answered "machine-bound or
    /// not" could not represent "anchored to a home folder, so it travels to everyone the project is shared with,
    /// each resolved against their own" as anything but a bare false, indistinguishable from a portable repo-relative
    /// reference — which is exactly how the AC-244 classifier and this class ended up disagreeing about a
    /// <c>~/...</c> reference without either side noticing. (Raymond, AC-605 review round: this doc comment itself
    /// used to say "travels with <em>them</em>", the same one-person framing the class rewrite below now retires.)
    /// <para>
    /// Deliberately takes no <paramref name="sourceDirectory"/> and mirrors
    /// <c>Cockpit.Plugin.Depot.ProjectDefinition.ProjectResourcePortabilityClassifier.Classify</c> exactly (AC-605
    /// criterion 4) — a stored reference's shape alone says what it is; whether an absolute reference happens to sit
    /// inside the current project folder is a different question, answered by <see cref="SuggestRepoRelativeFix"/>.
    /// Null only for a blank reference — nothing to classify.
    /// </para>
    /// </summary>
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

    /// <summary>
    /// The repo-relative form <paramref name="reference"/> should have been stored as, when it is a fully qualified
    /// path that lives inside <paramref name="sourceDirectory"/> but never actually went through
    /// <see cref="ToStoredReference"/> — hand-typed, or written into a hand-edited <c>cockpit.json</c> (AC-605
    /// criterion 5). Reuses <see cref="ToStoredReference"/> itself to compute it rather than duplicating the
    /// "is it under the folder" check a second time, without ever calling it on the operator's behalf — see this
    /// class's own remarks on why only a picked path is rewritten automatically. Null when there is nothing to fix:
    /// <paramref name="reference"/> is not an absolute path, is already outside <paramref name="sourceDirectory"/>
    /// (nothing to anchor it to), or malformed the same way <see cref="ToStoredReference"/> already fails open for.
    /// </summary>
    public static string? SuggestRepoRelativeFix(string? sourceDirectory, string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference) || !Path.IsPathFullyQualified(reference))
        {
            return null;
        }

        var stored = ToStoredReference(sourceDirectory, reference);
        return string.Equals(stored, reference, StringComparison.Ordinal) ? null : stored;
    }

    /// <summary>
    /// Whether <paramref name="reference"/> is anchored to the operator's home directory (AC-605) — exactly <c>~</c>
    /// itself, or anything starting with <c>~/</c>. Deliberately narrower than a bare
    /// <c>reference.StartsWith('~')</c> (what the AC-244 classifier used before this ticket): <c>~henk/x</c> is a
    /// POSIX shell's "some other user's home" expansion, a shell feature .NET's own path APIs know nothing about —
    /// treating it as this operator's home would resolve to the wrong place silently. Only <c>~</c> and <c>~/...</c>
    /// are a supported anchor form (Raymond's decision, AC-605); anything else starting with <c>~</c> is left as
    /// ordinary text, the same as any other reference this class does not recognise a shape for.
    /// </summary>
    public static bool IsHomeAnchored(string? reference) =>
        reference is "~" || (reference?.StartsWith("~/", StringComparison.Ordinal) ?? false);

    /// <summary>
    /// <paramref name="reference"/> with its <c>~</c> anchor resolved to an actual path on this machine (AC-605
    /// criterion 1) — the one place that does this; every caller that needs an anchored reference as a real
    /// filesystem path (<see cref="Infrastructure.Projects.ProjectResourceProbe"/>,
    /// <see cref="Infrastructure.Projects.ProjectInstructionContentReader"/> actually opening a row's file, the
    /// project editor's own folder picker seeding itself from a typed Memory-folder reference) goes through here
    /// rather than expanding <c>~</c> itself — a caller that skips this and hands a raw <c>"~/..."</c> string
    /// straight to a filesystem API gets an exception on every platform, not a resolved path (measured: exactly
    /// what <see cref="Infrastructure.Projects.ProjectInstructionContentReader"/> did until AC-605's own review
    /// round caught it — this doc comment asserted the reader "goes through here" before it actually did).
    /// <see cref="Environment.SpecialFolder.UserProfile"/>
    /// rather than the <c>HOME</c>/<c>USERPROFILE</c> environment variable directly: it already resolves to
    /// <c>$HOME</c> on Linux/macOS and the profile folder on Windows, so this needs no platform branch of its own.
    /// <para>
    /// Returns <paramref name="reference"/> unchanged when it is not home-anchored (see <see cref="IsHomeAnchored"/>)
    /// — including <c>~henk/x</c>, which this deliberately never expands (see <see cref="IsHomeAnchored"/>'s own
    /// remarks) — or when resolving it throws, the same fail-open rule <see cref="ToStoredReference"/> already
    /// follows for a reference this runtime's filesystem APIs refuse outright (a NUL byte, say).
    /// </para>
    /// <para>
    /// Does not special-case a suffix that climbs back out of the home directory (<c>~/../../etc/passwd</c>) — see
    /// this class's own remarks on why a bounds check is not this method's job.
    /// </para>
    /// </summary>
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
