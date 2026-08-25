using System.Diagnostics;
using Cockpit.Core.Projects;

namespace Cockpit.Infrastructure.Projects;

// Checks a project's `ProjectResource` rows for a fully-qualified path found missing (AC-484); runs
// synchronously on a UI thread, so its running time is a responsiveness concern. A scheme, relative, or UNC
// reference is skipped (a UNC check can be a 1200ms+ network round trip); silence beats calling one broken.
public static class ProjectResourceProbe
{
    // Ceiling on how long `FindUnresolved` spends checking every row together, not per-row — a UI-thread caller
    // cares about total wait. 200 ms covers the ordinary case. Not a hard wall: thread start-up and wait
    // granularity per row add real time on top, measured up to ~208 ms wall-clock in practice.
    private static readonly TimeSpan _TimeBudget = TimeSpan.FromMilliseconds(200);

    // The `ProjectResource.Reference` of every checked row that is fully qualified and missing. Never throws —
    // a probe is a convenience, not a dependency. A row unanswered when the time budget runs out, or whose
    // `ReachesSessions` is false, is left out rather than reported broken. Params `timeBudget`/`pathExists` are test-only hooks.
    public static IReadOnlyCollection<string> FindUnresolved(
        IEnumerable<ProjectResource> resources,
        TimeSpan? timeBudget = null,
        Func<string, bool>? pathExists = null)
    {
        var budget = timeBudget ?? _TimeBudget;
        var exists = pathExists ?? _DefaultPathExists;
        var unresolved = new HashSet<string>(StringComparer.Ordinal);
        var stopwatch = Stopwatch.StartNew();

        foreach (var reference in resources
            .Where(resource => resource.ReachesSessions)
            .Select(resource => resource.Reference)
            .Distinct(StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(reference))
            {
                continue;
            }

            try
            {
                // A <scheme>:<value> reference is the plugin's to judge, not this probe's — see the class remarks.
                if (ProjectMemoryRef.TryParse(reference, out _, out _))
                {
                    continue;
                }

                // AC-605 criterion 2: a `~`-anchored reference resolves to a real, checkable path on its own,
                // without needing SourceDirectory — see the class remarks. Anything else that is still not fully
                // qualified is AC-485's concern, not this probe's, and is skipped exactly as before.
                var isHomeAnchored = ProjectResourcePathPortability.IsHomeAnchored(reference);
                if (!isHomeAnchored && !Path.IsPathFullyQualified(reference))
                {
                    continue;
                }

                var resolved = isHomeAnchored ? ProjectResourcePathPortability.ResolveHomeAnchor(reference) : reference;

                // A UNC path is fully qualified but this probe cannot afford to check one cheaply — see the class
                // remarks on the 1282 ms measurement that documents why.
                if (resolved.StartsWith(@"\\", StringComparison.Ordinal))
                {
                    continue;
                }

                var remaining = budget - stopwatch.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    // The shared time budget for every row together is already spent. Stopping here rather than
                    // skipping ahead to a (possibly cheaper) later row keeps the promise simple: once the budget
                    // is gone, nothing further is said about any remaining row, broken or not.
                    break;
                }

                // Runs the check on its own dedicated Thread (not Task.Run/ThreadPool) with a capped wait: a
                // mapped drive over a slow link can block past the budget even when not literally UNC, and
                // blocking a ThreadPool thread on work queued to the same pool starves it under load.
                var completed = new ManualResetEventSlim(initialState: false);
                var found = false;
                var thread = new Thread(() =>
                {
                    try
                    {
                        found = exists(resolved);
                    }
                    finally
                    {
                        completed.Set();
                    }
                })
                {
                    IsBackground = true,
                };
                thread.Start();

                if (!completed.Wait(remaining))
                {
                    // Unanswered within budget: left out, not reported broken, same silent-over-wrong rule as
                    // elsewhere. The background thread keeps running (never blocks process exit); `completed` is
                    // deliberately left undisposed — disposing here could race its `finally`'s `Set()` into a crash.
                    break;
                }

                if (!found)
                {
                    unresolved.Add(reference);
                }

                completed.Dispose();
            }
            catch
            {
                // A reference this runtime cannot even parse as a path (invalid characters, too long, …) is not
                // this probe's to call broken — better silent than wrongly accusing a value of the wrong kind.
            }
        }

        return unresolved;
    }

    private static bool _DefaultPathExists(string reference) => File.Exists(reference) || Directory.Exists(reference);
}
