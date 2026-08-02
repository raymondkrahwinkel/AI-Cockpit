using System.Diagnostics;
using Cockpit.Core.Projects;

namespace Cockpit.Infrastructure.Projects;

/// <summary>
/// Checks a project's <see cref="ProjectResource"/> rows for a reference that names an absolute, existing-or-not
/// filesystem path and finds it missing (AC-484) — the one piece of I/O
/// <see cref="Cockpit.Core.Sessions.SessionStartDefaults.Resolve"/> deliberately never does itself (see that
/// method's own remarks on <c>unresolvedReferences</c>: purity is a property of that class, not an oversight here).
/// The layer that assembles an actual launch (<c>ProjectQuickStart</c>, the New-session dialog's Start) runs this
/// once and hands the result in as plain data — both call sites do so synchronously from a UI thread
/// (<c>NewSessionDialogViewModel.Confirm()</c>'s <c>[RelayCommand]</c>, and <c>ProjectQuickStart.ComposeAsync</c>
/// after its own <c>ConfigureAwait(true)</c>), so this method is one of a very small number in the codebase whose
/// own running time is a UI-responsiveness concern, not merely a correctness one.
/// <para>
/// Scope is deliberately narrow — only a reference that is a <em>fully qualified</em> path
/// (<see cref="Path.IsPathFullyQualified(string)"/>) is checked at all:
/// </para>
/// <list type="bullet">
/// <item><description>
/// A <c>&lt;scheme&gt;:&lt;value&gt;</c> reference (<see cref="ProjectMemoryRef.TryParse"/>) is never checked —
/// only the plugin that registered that scheme could judge whether its value is reachable, and this probe knows
/// nothing about plugins.
/// </description></item>
/// <item><description>
/// A relative path is never checked either — whether a relative path travels with the project it is relative to is
/// AC-485's question, not this one's. A <c>~</c>-anchored reference is the one exception (AC-605 criterion 2): it is
/// not fully qualified as far as <see cref="Path.IsPathFullyQualified(string)"/> is concerned, but this probe has
/// enough on its own to resolve it (<see cref="ProjectResourcePathPortability.ResolveHomeAnchor"/> needs only the
/// reference itself, no <c>SourceDirectory</c>) and does — a <c>~</c> row that does not exist on this machine gets
/// the same "could not be found" treatment as any other missing absolute path, rather than being silently skipped
/// the way a repo-relative reference still is.
/// </description></item>
/// <item><description>
/// A UNC path (<c>\\host\share\...</c>) is fully qualified but never checked either (AC-484 review, MUST-FIX 4): an
/// unreachable host turns <see cref="File.Exists(string)"/>/<see cref="Directory.Exists(string)"/> into a network
/// round trip — measured at 1282 ms for a single unreachable host, synchronous, with no cancellation, which on a
/// UI thread is not a slow answer but a frozen window. Better to say nothing about a reference this probe cannot
/// afford to judge cheaply than to block the caller for however long the network takes to give up.
/// </description></item>
/// </list>
/// <para>
/// Better to say nothing about a reference this probe cannot fairly judge than to call it broken when it might
/// simply be a kind — a scheme, a relative path, a UNC path — outside what a cheap filesystem check can answer.
/// </para>
/// <para>
/// AC-484 review (FIX 7) — a platform asymmetry deliberately left as-is rather than "fixed": whether a path counts
/// as fully qualified depends on the OS this runtime is on. <c>Path.IsPathFullyQualified("/home/raymond/Notes")</c>
/// is <c>false</c> on Windows, so a POSIX-shaped reference is silently never checked there — and the reverse holds
/// for a <c>C:\...</c> reference interpreted on Linux. There is no project-portable notion of "absolute path" for
/// this probe to fall back on instead, so a project that keeps its resources on the platform it was not authored
/// on gets no unresolved-reference warning at all for those rows, on either side. All of this class's current tests
/// use POSIX-shaped paths, which is exactly why this gap does not show up in them. Separately: this probe cannot
/// and does not distinguish "does not exist" from "exists but this process may not read it" — both
/// <see cref="File.Exists(string)"/> and <see cref="Directory.Exists(string)"/> answer <c>false</c> for a path
/// blocked by permissions, the same as for one that is simply not there.
/// </para>
/// </summary>
public static class ProjectResourceProbe
{
    /// <summary>
    /// The ceiling on how long <see cref="FindUnresolved"/> may spend checking every row together (AC-484 review,
    /// MUST-FIX 4) — not a per-row budget, because a caller waiting on a UI thread cares about the total wait, not
    /// how it was divided among rows. 200 ms is enough for the ordinary case (a handful of local paths, each
    /// answered by the OS in well under a millisecond) while staying far short of anything a human would notice as
    /// the dialog hanging.
    /// <para>
    /// AC-484 confirming round (FIX 4): this was called a "hard" ceiling, and <see cref="FindUnresolved"/>'s own doc
    /// claimed it "never blocks past its time budget" — neither is quite true. This is a per-row deadline the loop
    /// checks before starting each row's own dedicated <see cref="Thread"/>; once a row's thread has been started,
    /// this method still waits out that row's own <see cref="ManualResetEventSlim.Wait(TimeSpan)"/>, and starting
    /// the thread plus that wait's own granularity both take a little real time of their own that this budget does
    /// not account for. Measured (AC-484 confirming round) at up to 207.7 ms wall-clock over ten runs against the
    /// nominal 200 — close enough that it does not read as the dialog hanging, but a real overrun rather than the
    /// hard wall the earlier wording promised.
    /// </para>
    /// </summary>
    private static readonly TimeSpan _TimeBudget = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// The <see cref="ProjectResource.Reference"/> value of every row in <paramref name="resources"/> that is a
    /// fully qualified path and does not exist as either a file or a directory. Never throws — a probe is a
    /// convenience, not a dependency (the same line the bundled-plugin installer draws): whatever this method finds
    /// out about a reference, it finds out by returning normally, never by raising. Stays close to its time budget
    /// either (AC-484 review, MUST-FIX 4; see <see cref="_TimeBudget"/>'s own remarks on how close), which is a
    /// separate promise from "never throws": a call that hangs for a second on an unreachable network host raises
    /// no exception at all, so "never throws" alone would say nothing about the one failure mode that actually
    /// froze the caller's UI thread. A row left unanswered when the budget runs out is treated the same way as one
    /// this probe declines to judge at all — left out, not reported broken.
    /// <para>
    /// AC-484 confirming round (FIX 4): this used to also claim that a reference this runtime cannot even parse as
    /// a path is "left out of the result rather than reported broken" — true of whatever narrower case still throws
    /// inside the <c>catch</c> below, but not of the two cases actually measured here: a path with invalid
    /// characters (<c>C:\bad&lt;&gt;|?*\0name.md</c>) and an absurdly long one (32,000 characters). On .NET 10,
    /// <see cref="File.Exists(string)"/> and <see cref="Directory.Exists(string)"/> return <c>false</c> rather than
    /// throwing for either, so both flow through as an ordinary "does not exist" and end up <em>in</em> the result —
    /// reported unresolved, not silently skipped. The <c>catch</c> block is a narrower safety net than this comment
    /// used to describe, kept for whatever case still does throw, while the malformed-path case it was written
    /// about no longer reaches it at all.
    /// </para>
    /// <para>
    /// A row with <see cref="ProjectResource.ReachesSessions"/> set to false is filtered out before any I/O runs
    /// (AC-484 review, MUST-FIX 4): a row that will never reach a starting session's prompt gains nothing from
    /// being checked, so it is not worth spending any of the shared time budget on.
    /// </para>
    /// </summary>
    /// <param name="resources">The rows to check. A row that does not reach sessions is skipped before any I/O.</param>
    /// <param name="timeBudget">
    /// Overrides <see cref="_TimeBudget"/> for the whole call — a test's own hook for proving the budget is
    /// enforced without needing an actually slow filesystem to prove it against. Null (the default) is production
    /// behavior.
    /// </param>
    /// <param name="pathExists">
    /// Overrides the existence check itself — another test-only hook, letting a test simulate a check that never
    /// returns in time without needing a genuinely unreachable UNC host to do it. Null (the default) is
    /// <see cref="File.Exists(string)"/> or <see cref="Directory.Exists(string)"/>, exactly as before.
    /// </param>
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

                // Run the check on its own dedicated thread and cap how long this call waits on it, rather than
                // calling File.Exists/Directory.Exists directly here: a check against an unreachable network path
                // (a mapped drive letter over the same slow link a UNC path would use, for instance) can still
                // block far longer than the budget even though the reference itself is not literally a UNC string,
                // and this is the one place left to catch that.
                //
                // A dedicated Thread rather than Task.Run/the shared ThreadPool on purpose: this method is called
                // from a UI thread that itself may be a ThreadPool thread (an async continuation), and blocking one
                // ThreadPool thread while waiting on work queued to the very same pool is exactly the
                // sync-over-async shape that starves it under load — proven by this class's own test suite, where
                // an ordinary "does this path exist" check missed its 200 ms budget and came back empty only
                // because the pool was busy servicing other tests' queued work. A raw thread does not compete for
                // that pool at all.
                //
                // That safety is not free: starting a dedicated OS thread per row costs real time on top of
                // whatever the check itself takes. Measured (AC-484 confirming round): 500 ordinary rows that
                // individually resolve in 7.2 ms combined raw take 69 ms once each goes through its own Thread and
                // ManualResetEventSlim wait here — nearly a 10x multiplier from thread start-up and wait
                // granularity alone, before the check itself costs anything. At 2000 rows the 200 ms budget above
                // runs out partway through and 272 rows are left unanswered (not wrongly accused — see the remarks
                // above on what an unanswered row means). None of this matters at the sizes a project actually has
                // (under ten resource rows answers in about 2 ms total), but it is the reason this probe would not
                // scale unchanged to a caller handing it a large list.
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
                    // This row did not answer within its share of the budget. Not reporting it follows the same
                    // "better silent than wrongly accusing" rule this class already applies to a path it cannot
                    // parse at all — an unanswered check is not evidence the reference is broken, and the caller
                    // waiting on this result should not wait any longer to be told that. The background thread is
                    // left to finish (or never finish) on its own; a background thread never blocks the process
                    // from exiting, and nothing further here waits on it.
                    //
                    // `completed` is deliberately not disposed on this path (AC-484 confirming round, FIX 4): the
                    // abandoned thread's `finally` still calls `completed.Set()` whenever it does eventually finish,
                    // and disposing here would race that call — an ObjectDisposedException thrown on a background
                    // thread with nothing to catch it would crash the process outright, far worse than leaving one
                    // handle for the finalizer on the rare timeout path. Every row that does answer in time disposes
                    // normally below.
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
