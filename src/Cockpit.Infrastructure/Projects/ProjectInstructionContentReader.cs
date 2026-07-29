using Cockpit.Core.Projects;

namespace Cockpit.Infrastructure.Projects;

/// <summary>
/// Reads an <see cref="ProjectResourceRole.Instructions"/> row's file content at session start (AC-486), for
/// whichever rows ticked <see cref="ProjectResource.SendsContent"/> — the one piece of I/O
/// <see cref="Cockpit.Core.Sessions.SessionStartDefaults.Resolve"/> deliberately never does itself (purity is a
/// property that class keeps on purpose; see its own remarks on <c>unresolvedReferences</c> and
/// <see cref="ProjectResourceProbe"/>'s class remarks for the same reasoning already applied to an existence check).
/// The layer that assembles an actual launch (<c>ProjectQuickStart</c>, the New-session dialog's Start) runs this
/// once, next to its own <see cref="ProjectResourceProbe.FindUnresolved"/> call, and hands the result in to
/// <see cref="Cockpit.Core.Sessions.SessionStartDefaults.Resolve"/> as plain data.
/// </summary>
public static class ProjectInstructionContentReader
{
    /// <summary>
    /// The most this reads of any one file, in bytes, before giving up on it as too large to ever matter rather
    /// than loading it and finding out afterwards. Deliberately a fixed constant rather than "however much of the
    /// shared prompt ceiling happens to be free right now": that share moves with every call — how many other rows
    /// and blocks are competing for the same 5,500-character <c>SessionStartDefaults.ProjectContributionBudget</c> —
    /// while a file on disk does not get smaller because other rows are also asking for room. A row's content is
    /// carried whole or never at all (AC-486 — an instruction is never cut halfway), so nothing this reader could
    /// ever hand back is worth more than that whole ceiling, however small the actual per-call share turns out to
    /// be once <see cref="Cockpit.Core.Sessions.SessionStartDefaults.Resolve"/> divides it up.
    /// <para>
    /// 32 KB is sized against the worst case, not the common one. At four bytes per character — the most a single
    /// UTF-8 code point ever costs — 32,768 bytes still decodes to at least 8,192 characters, comfortably above the
    /// 5,500-character ceiling with room left over for the citation and "captured at session start" framing this
    /// reader's caller wraps around it. Ordinary instruction text (English prose, at most a couple of bytes per
    /// character) decodes to up to the full 32,768 characters from the same budget — six times the ceiling. Either
    /// way, a file whose raw bytes already exceed this could never fit the budget regardless of how it happens to
    /// be encoded, so reading further would only cost memory for nothing this reader is ever allowed to use in
    /// full. The check itself is metadata only (a file length, not its bytes), so a file that fails it is never
    /// opened at all — a 10 MB file costs the same length check as a 10-byte one.
    /// </para>
    /// </summary>
    private const int _MaxReadBytes = 32 * 1024;

    /// <summary>
    /// The file content read for every row in <paramref name="resources"/> whose <see cref="ProjectResource.Role"/>
    /// is <see cref="ProjectResourceRole.Instructions"/>, whose <see cref="ProjectResource.SendsContent"/> is ticked,
    /// and whose <see cref="ProjectResource.ReachesSessions"/> is true — keyed by
    /// <see cref="ProjectResource.Reference"/>. Never throws and never blocks a session from starting: a row this
    /// method could not read (missing, larger than <see cref="_MaxReadBytes"/>, a permissions error, a race where
    /// the file is removed between the length check and the read) simply has no entry in the result, the same
    /// convenience-not-a-dependency line <see cref="ProjectResourceProbe"/> and the bundled-plugin installer both
    /// draw. <see cref="Cockpit.Core.Sessions.SessionStartDefaults.Resolve"/> reads a missing entry for a ticked row
    /// as "not included" and says so, rather than this method having to explain why.
    /// </summary>
    /// <param name="resources">The rows to read from. A row outside the Instructions/SendsContent/ReachesSessions combination is skipped before any I/O.</param>
    /// <param name="fileLength">
    /// Overrides the file-size check — a test's own hook for proving the size cap is enforced without needing an
    /// actual 32 KB-plus file on disk to prove it against. Null (the default) is <c>new FileInfo(path).Length</c>.
    /// </param>
    /// <param name="readAllText">
    /// Overrides the read itself — another test-only hook, letting a test simulate an unreadable or vanished file
    /// without needing real filesystem permissions or a genuine race to do it. Null (the default) is
    /// <see cref="File.ReadAllText(string)"/>, which detects the file's own encoding the same way it always has.
    /// </param>
    public static IReadOnlyDictionary<string, string> Read(
        IEnumerable<ProjectResource> resources,
        Func<string, long>? fileLength = null,
        Func<string, string>? readAllText = null)
    {
        var length = fileLength ?? _DefaultLength;
        var readText = readAllText ?? _DefaultReadAllText;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var resource in resources)
        {
            if (resource.Role != ProjectResourceRole.Instructions || !resource.SendsContent || !resource.ReachesSessions)
            {
                continue;
            }

            var reference = resource.Reference;
            if (string.IsNullOrWhiteSpace(reference) || result.ContainsKey(reference))
            {
                // A blank reference has nothing to read, and a reference already read (two rows naming the same
                // file) costs no second read — the dictionary already answers for it.
                continue;
            }

            try
            {
                var size = length(reference);
                if (size < 0 || size > _MaxReadBytes)
                {
                    // Either a negative size a test hook used to signal "not there", or a file too large to ever
                    // fit the shared ceiling regardless of encoding (see _MaxReadBytes) — either way, left out
                    // rather than opened for nothing.
                    continue;
                }

                result[reference] = readText(reference);
            }
            catch
            {
                // Unreadable for any reason this runtime can throw for — gone by the time the read actually ran
                // (a race between the length check above and this line), permissions, a path the filesystem
                // rejects. A read is a convenience, not a dependency: the session starts regardless, and
                // SessionStartDefaults.Resolve tells it this row's content did not make it in rather than pretending
                // it was never asked for.
            }
        }

        return result;
    }

    private static long _DefaultLength(string path) => new FileInfo(path).Length;

    private static string _DefaultReadAllText(string path) => File.ReadAllText(path);
}
