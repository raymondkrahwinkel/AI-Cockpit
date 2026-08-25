using Cockpit.Core.Projects;

namespace Cockpit.Infrastructure.Projects;

// Reads an `ProjectResourceRole.Instructions` row's file content at session start (AC-486), kept out of
// `Cockpit.Core.Sessions.SessionStartDefaults.Resolve` to keep that method free of I/O. AC-605: resolves
// a `~`-anchored `ProjectResource.Reference` before the size check or read touches disk.
public static class ProjectInstructionContentReader
{
    // Max bytes read per file before giving up on it as too large to matter. Fixed rather than tied to the
    // shared 5,500-character `SessionStartDefaults.ProjectContributionBudget`; 32 KB covers even UTF-8's
    // worst-case 4 bytes/char with room to spare. Metadata-only check, so an oversized file is never opened.
    private const int _MaxReadBytes = 32 * 1024;

    // Reads content for every Instructions/SendsContent/ReachesSessions row, keyed by Reference. Never throws:
    // an unreadable row (missing, oversized, permission error, mid-race removal) simply has no entry, and
    // Resolve reads that as "not included". `fileLength`/`readAllText` are test-only override hooks.
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
                // Blank reference: nothing to read. Already-seen reference: skip re-reading — keyed by the
                // stored reference, not the resolved path, since Resolve looks each row up by its own Reference.
                continue;
            }

            // AC-605: the one place a "~"-anchored reference is resolved to a real filesystem path. The result
            // dictionary stays keyed by `reference`, not `path` — Resolve's own lookup is by Reference, so
            // keying by the resolved path would make the content unfindable by the key its caller uses.
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

                // Read on a thread of its own with a deadline, like ProjectResourceProbe: this runs synchronously
                // on the Start thread, and a cloud-sync placeholder (OneDrive, Nextcloud "online-only") downloads
                // on open, so a read can block where an existence check does not. An overrun is just dropped.
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
                // Unreadable for any reason (mid-race removal, permissions, rejected path). A read is a
                // convenience, not a dependency: the session starts regardless, and Resolve reports this
                // row's content as not included.
            }
        }

        return result;
    }

    // Per-file read budget, same order as ProjectResourceProbe's and for the same reason (runs on the Start
    // thread), but applied per file so one slow row doesn't cost the rest. Not a hard wall — thread startup
    // and wait granularity add real overrun on top.
    private static readonly TimeSpan _ReadBudget = TimeSpan.FromMilliseconds(300);

    private static long _DefaultLength(string path) => new FileInfo(path).Length;

    private static string _DefaultReadAllText(string path) => File.ReadAllText(path);
}
