using Avalonia.Threading;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.GitHubPullRequests;

/// <summary>
/// Watches for a pull request of yours going from not-merged to merged, and fires the workflow trigger when one does
/// (#69). GitHub will not tell us; there is no webhook a desktop app can receive, so it is asked — every few minutes,
/// with the answer compared against the last one.
/// <para>
/// The comparison is the whole thing (<see cref="MergedPullRequests"/>): a poll sees the world, not the change. And
/// the first look fires nothing, because every pull request you have ever merged is new to a process that just
/// started, and a flow that ran forty times the moment the cockpit opened would be the last time you armed it.
/// </para>
/// </summary>
internal sealed class MergedPullRequestWatcher : IDisposable
{
    // Merges are not urgent and gh's search is not free. Five minutes is soon enough to be useful and rare enough that
    // nobody notices it happening.
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly ICockpitHost _host;
    private readonly GitHubPrGhClient _client = new();
    private readonly DispatcherTimer _timer;

    private HashSet<string> _seen = new(StringComparer.Ordinal);
    private bool _primed;
    private bool _looking;

    public MergedPullRequestWatcher(ICockpitHost host)
    {
        _host = host;

        _timer = new DispatcherTimer { Interval = Interval };
        _timer.Tick += (_, _) => _ = _LookAsync();
        _timer.Start();

        _ = _LookAsync();
    }

    public void Dispose() => _timer.Stop();

    private async Task _LookAsync()
    {
        // A look that takes longer than the interval must not have a second one started on top of it: two answers
        // racing to update what has been seen is how a merge fires twice, or not at all.
        if (_looking)
        {
            return;
        }

        _looking = true;

        try
        {
            var merged = await _client.SearchMergedAsync(CancellationToken.None);
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
            _looking = false;
        }
    }
}
