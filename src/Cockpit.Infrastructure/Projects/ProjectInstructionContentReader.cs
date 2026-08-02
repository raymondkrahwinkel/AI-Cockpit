using Cockpit.Core.Projects;

namespace Cockpit.Infrastructure.Projects;

// Reads an `ProjectResourceRole.Instructions` row's file content at session start (AC-486), for
// whichever rows ticked `ProjectResource.SendsContent` — the one piece of I/O
// `Cockpit.Core.Sessions.SessionStartDefaults.Resolve` deliberately never does itself (purity is a
// property that class keeps on purpose; see its own remarks on `unresolvedReferences` and
// `ProjectResourceProbe`'s class remarks for the same reasoning already applied to an existence check).
// The layer that assembles an actual launch (`ProjectQuickStart`, the New-session dialog's Start) runs this
// once, next to its own `ProjectResourceProbe.FindUnresolved` call, and hands the result in to
// `Cockpit.Core.Sessions.SessionStartDefaults.Resolve` as plain data.
//
// AC-605: a `~`-anchored `ProjectResource.Reference` is resolved to a real path before either the
// size check or the read touches disk (see the resolve call inside `Read`) — a row this method never
// resolved would classify as portable, sit silently in a shared `.cockpit/project.json`, and then simply never
// reach a session, since `new FileInfo("~/x")` and `File.ReadAllText("~/x")` both throw for a path no
// filesystem API expands on its own; that failure was caught by the same `catch` this class already carries
// for an unreadable file, so it read as "not found" rather than "never actually tried". Found in review, not by a
// test this class already had.
public static class ProjectInstructionContentReader
{
    // The most this reads of any one file, in bytes, before giving up on it as too large to ever matter rather
    // than loading it and finding out afterwards. Deliberately a fixed constant rather than "however much of the
    // shared prompt ceiling happens to be free right now": that share moves with every call — how many other rows
    // and blocks are competing for the same 5,500-character `SessionStartDefaults.ProjectContributionBudget` —
    // while a file on disk does not get smaller because other rows are also asking for room. A row's content is
    // carried whole or never at all (AC-486 — an instruction is never cut halfway), so nothing this reader could
    // ever hand back is worth more than that whole ceiling, however small the actual per-call share turns out to
    // be once `Cockpit.Core.Sessions.SessionStartDefaults.Resolve` divides it up.
    //
    // 32 KB is sized against the worst case, not the common one. At four bytes per character — the most a single
    // UTF-8 code point ever costs — 32,768 bytes still decodes to at least 8,192 characters, comfortably above the
    // 5,500-character ceiling with room left over for the citation and "captured at session start" framing this
    // reader's caller wraps around it. Ordinary instruction text (English prose, at most a couple of bytes per
    // character) decodes to up to the full 32,768 characters from the same budget — six times the ceiling. Either
    // way, a file whose raw bytes already exceed this could never fit the budget regardless of how it happens to
    // be encoded, so reading further would only cost memory for nothing this reader is ever allowed to use in
    // full. The check itself is metadata only (a file length, not its bytes), so a file that fails it is never
    // opened at all — a 10 MB file costs the same length check as a 10-byte one.
    private const int _MaxReadBytes = 32 * 1024;

    // The file content read for every row in `resources` whose `ProjectResource.Role`
    // is `ProjectResourceRole.Instructions`, whose `ProjectResource.SendsContent` is ticked,
    // and whose `ProjectResource.ReachesSessions` is true — keyed by
    // `ProjectResource.Reference`. Never throws and never blocks a session from starting: a row this
    // method could not read (missing, larger than `_MaxReadBytes`, a permissions error, a race where
    // the file is removed between the length check and the read) simply has no entry in the result, the same
    // convenience-not-a-dependency line `ProjectResourceProbe` and the bundled-plugin installer both
    // draw. `Cockpit.Core.Sessions.SessionStartDefaults.Resolve` reads a missing entry for a ticked row
    // as "not included" and says so, rather than this method having to explain why.
    //
    // `resources`: The rows to read from. A row outside the Instructions/SendsContent/ReachesSessions combination is skipped before any I/O.
    // `fileLength`:
    // Overrides the file-size check — a test's own hook for proving the size cap is enforced without needing an
    // actual 32 KB-plus file on disk to prove it against. Null (the default) is `new FileInfo(path).Length`.
    // Called with the row's `ProjectResource.Reference` resolved through
    // `Core.Projects.ProjectResourcePathPortability.ResolveHomeAnchor` (AC-605), never the stored text
    // itself — a test exercising a `~`-anchored row asserts against whatever path that reference actually
    // resolves to on the box the test runs on, not the literal `"~/..."` string.
    // `readAllText`:
    // Overrides the read itself — another test-only hook, letting a test simulate an unreadable or vanished file
    // without needing real filesystem permissions or a genuine race to do it. Null (the default) is
    // `File.ReadAllText(string)`, which detects the file's own encoding the same way it always has.
    // Called with the resolved path, the same as `fileLength` above.
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
                // stored text) costs no second read — the dictionary already answers for it. Keyed and deduplicated
                // on the stored reference, not the resolved path (see the remark on `path` below): two rows that
                // spell the same file two different ways (say "~/Notes/x.md" and its resolved absolute form) still
                // read it twice, once per row, which is correct — SessionStartDefaults.Resolve looks each row's
                // content up by that row's own Reference, so both rows need their own entry regardless of whether
                // they happen to land on the same file underneath.
                continue;
            }

            // AC-605 criterion 1: the one place a "~"-anchored reference is resolved to a real filesystem path —
            // every caller that treats a ProjectResource.Reference as a path must resolve it exactly this way
            // rather than re-deriving the rule; see ProjectResourcePathPortability.ResolveHomeAnchor's own remarks.
            // A no-op for anything that is not home-anchored, so this costs nothing for the ordinary repo-relative
            // or already-absolute case. The result dictionary itself stays keyed by `reference` (the stored,
            // unresolved text) rather than `path` — SessionStartDefaults.Resolve's own TryGetValue call looks a row
            // up by its own Reference, never by whatever this reader resolved that to internally, so keying by the
            // resolved path would make a "~/Notes/x.md" row's content unfindable by the very key its caller uses.
            var path = ProjectResourcePathPortability.ResolveHomeAnchor(reference);

            try
            {
                var size = length(path);
                if (size < 0 || size > _MaxReadBytes)
                {
                    // Either a negative size a test hook used to signal "not there", or a file too large to ever
                    // fit the shared ceiling regardless of encoding (see _MaxReadBytes) — either way, left out
                    // rather than opened for nothing.
                    continue;
                }

                // Read on a thread of its own with a deadline, for the same reason ProjectResourceProbe does: this
                // runs synchronously on the thread that handles Start, and a path whose read does not return
                // promptly freezes the window rather than costing a moment. The probe learned that from an
                // unreachable network path; the risk here is larger, because reading a file can block where merely
                // asking whether it exists does not — a cloud-sync placeholder (OneDrive, Nextcloud "online-only")
                // downloads on open, and on this operator's machines that is the normal way files are stored, not
                // an exotic case. A read that overruns is dropped, and Resolve then says the content did not make
                // it in, which is the same honest answer an unreadable file already produces.
                var done = new ManualResetEventSlim(initialState: false);
                string? text = null;
                var reader = new Thread(() =>
                {
                    try
                    {
                        text = readText(path);
                    }
                    catch
                    {
                        // Swallowed here so the wait below sees "did not answer" rather than an exception crossing
                        // a thread boundary; the outer catch cannot see this one's stack.
                    }
                    finally
                    {
                        done.Set();
                    }
                })
                {
                    IsBackground = true,
                    Name = "cockpit-instruction-read",
                };

                reader.Start();
                if (done.Wait(_ReadBudget) && text is not null)
                {
                    result[reference] = text;
                }
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

    // How long one file's read may take before it is given up on. Deliberately the same order as
    // `ProjectResourceProbe`'s own budget and for the same reason — this runs on the thread that
    // handles Start — but applied per file rather than to the whole batch: a project carries a handful of ticked
    // rows at most, and a single slow one should cost its own content, not everyone else's.
    //
    // Not a hard wall: starting the thread and the wait's own granularity are real time this figure does not
    // account for, the same overrun measured on the probe. It bounds the freeze; it does not abolish it.
    private static readonly TimeSpan _ReadBudget = TimeSpan.FromMilliseconds(300);

    private static long _DefaultLength(string path) => new FileInfo(path).Length;

    private static string _DefaultReadAllText(string path) => File.ReadAllText(path);
}
