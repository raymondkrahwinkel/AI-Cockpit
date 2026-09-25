using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.GitHubPullRequests;

// Watches for a pull request of yours going from not-merged to merged, and fires the workflow trigger when one does
// (#69). GitHub will not tell us; there is no webhook a desktop app can receive, so it is asked — every few minutes,
// with the answer compared against the last one.
//
// The comparison is the whole thing (`MergedPullRequests`): a poll sees the world, not the change. And
// the first look fires nothing, because every pull request you have ever merged is new to a process that just
// started, and a flow that ran forty times the moment the cockpit opened would be the last time you armed it.
//
// AC-1396: the clock is `TimeProvider`, not Avalonia's dispatcher — the backend part has no window to tick on — so
// a look now starts on a threadpool thread.
internal sealed class MergedPullRequestWatcher : IDisposable
{
    // Merges are not urgent and gh's search is not free. Five minutes is soon enough to be useful and rare enough that
    // nobody notices it happening.
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly ICockpitHost _host;
    private readonly Func<CancellationToken, Task<IReadOnlyList<GitHubPullRequest>>> _searchMerged;
    private readonly ITimer _timer;

    private HashSet<string> _seen = new(StringComparer.Ordinal);
    private bool _primed;

    // An int, not a bool: two ticks can now overlap on the threadpool, where both could read a bool false before
    // either set it.
    private int _looking;

    public MergedPullRequestWatcher(ICockpitHost host)
        : this(host, new GitHubPrGhClient().SearchMergedAsync, TimeProvider.System)
    {
    }

    // Test seam: a controllable clock and a search that needs no gh, so a tick is provable without either.
    internal MergedPullRequestWatcher(
        ICockpitHost host,
        Func<CancellationToken, Task<IReadOnlyList<GitHubPullRequest>>> searchMerged,
        TimeProvider time)
    {
        _host = host;
        _searchMerged = searchMerged;

        // Due time zero: the first look (which only primes) happens at startup, as it did before, not an interval later.
        _timer = time.CreateTimer(_ => _OnTick(), null, TimeSpan.Zero, Interval);
    }

    public void Dispose() => _timer.Dispose();

    private async void _OnTick()
    {
        try
        {
            await _LookAsync();
        }
        catch (Exception)
        {
            // A watcher must never be the reason the cockpit falls over; the next tick tries again.
        }
    }

    private async Task _LookAsync()
    {
        // A look that takes longer than the interval must not have a second one started on top of it: two answers
        // racing to update what has been seen is how a merge fires twice, or not at all.
        if (Interlocked.CompareExchange(ref _looking, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var merged = await _searchMerged(CancellationToken.None);
            var result = MergedPullRequests.Reconcile(merged, _seen, _primed);

            _seen = new HashSet<string>(result.Seen, StringComparer.Ordinal);
            _primed = true;

            foreach (var pullRequest in result.Merged)
            {
                _host.RaiseWorkflowTrigger(
                    PullRequestWorkflowSteps.MergedTrigger,
                    new Dictionary<string, string>
                    {
                        ["number"] = pullRequest.Number.ToString(),
                        ["repository"] = pullRequest.Repository,
                        ["title"] = pullRequest.Title,
                        ["url"] = pullRequest.Url,
                        ["author"] = pullRequest.Author,
                    });
            }
        }
        catch (Exception)
        {
            // No gh, no network, a rate limit: none of it is worth a toast every five minutes about a thing nobody
            // asked for. The next look tries again, and nothing has been remembered that did not happen.
        }
        finally
        {
            Interlocked.Exchange(ref _looking, 0);
        }
    }
}
