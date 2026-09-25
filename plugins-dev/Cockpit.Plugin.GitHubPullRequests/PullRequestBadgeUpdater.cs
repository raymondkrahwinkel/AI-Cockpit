using System.Text.Json;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.GitHubPullRequests;

// The counting half of the side-menu launcher's live "N / M" badge (AC-517, AC-516): what the two numbers are, the
// instant-on-signal refresh, and which review requests just arrived. Built once in
// `GitHubPullRequestsPlugin.Initialize` and disposed with the plugin, so it keeps counting whether or not a window
// is showing the badge.
//
// AC-1396: the badge itself and the "review requested" toast moved to the UI part (PullRequestBadge), fed by the
// BadgeChanged event this publishes. The debounce clock is `TimeProvider`, so its tick, like every snapshot update
// from the refresh source, lands on a threadpool thread — hence the lock around what those two share.
internal sealed class PullRequestBadgeUpdater : IDisposable
{
    // On top of the shared PullRequestRefreshSource's own background poll, a short debounce coalesces the burst
    // of lines a single `gh pr create` prints into one refresh — moved verbatim from the old side section/widget.
    private static readonly TimeSpan SignalDebounce = TimeSpan.FromSeconds(3);

    private readonly IPluginBackendChannel _channel;
    private readonly ICockpitSessionObserver _sessions;
    private readonly GitHubPullRequestsSettings _settings;
    private readonly PullRequestRefreshSource _refreshSource;
    private readonly ITimer _signalRefresh;

    // Guards the seen-set's read-reconcile-write and `_counts`: two snapshot updates landing together on the
    // threadpool must not both announce the same arrival (AC-1250), nor publish their counts out of order.
    private readonly Lock _gate = new();
    private PullRequestBadgeState _counts = PullRequestBadgeState.Unknown;

    // Arrivals published before any UI part claimed them: the first poll lands before UI parts initialise, and a
    // published event with no subscriber is dropped. Kept to what is still waiting, until the first Claim.
    private readonly Dictionary<string, GitHubPullRequest> _unclaimed = new(StringComparer.Ordinal);
    private bool _claimed;

    public PullRequestBadgeUpdater(
        IPluginBackendChannel channel,
        ICockpitSessionObserver sessions,
        GitHubPullRequestsSettings settings,
        PullRequestRefreshSource refreshSource)
        : this(channel, sessions, settings, refreshSource, TimeProvider.System)
    {
    }

    // Test seam: a controllable clock, so the debounce is provable without waiting for it.
    internal PullRequestBadgeUpdater(
        IPluginBackendChannel channel,
        ICockpitSessionObserver sessions,
        GitHubPullRequestsSettings settings,
        PullRequestRefreshSource refreshSource,
        TimeProvider time)
    {
        _channel = channel;
        _sessions = sessions;
        _settings = settings;
        _refreshSource = refreshSource;
        _signalRefresh = time.CreateTimer(_ => _OnSignalQuiet(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        _refreshSource.Updated += _OnUpdated;
        _sessions.OutputProduced += _OnSessionOutput;

        _Apply(_refreshSource.Current);
    }

    // What the badge shows right now, arrivals left out: those were announced once, when they were published.
    public PullRequestBadgeState Counts
    {
        get
        {
            lock (_gate)
            {
                return _counts;
            }
        }
    }

    // The badge-counts answer: the counts, plus every arrival nobody has been shown yet. The first call hands the
    // announcing over to the BadgeChanged events for good.
    public PullRequestBadgeState Claim()
    {
        lock (_gate)
        {
            _claimed = true;
            var unclaimed = _counts with { Arrived = [.. _unclaimed.Values] };
            _unclaimed.Clear();
            return unclaimed;
        }
    }

    private void _OnSessionOutput(object? sender, SessionOutputText output)
    {
        if (PullRequestSignalDetector.ContainsSignal(output.Text))
        {
            // Re-arming restarts the wait, so a burst of lines is one refresh three seconds after its last line.
            _signalRefresh.Change(SignalDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    private async void _OnSignalQuiet()
    {
        try
        {
            await _refreshSource.RefreshAsync(forceRefresh: true);
        }
        catch (Exception)
        {
            // RefreshAsync already keeps its own failure as LastError; nothing here may take the plugin down.
        }
    }

    private void _OnUpdated(object? sender, PullRequestFeedSnapshot snapshot) => _Apply(snapshot);

    private void _Apply(PullRequestFeedSnapshot snapshot)
    {
        PullRequestBadgeState published;
        lock (_gate)
        {
            var result = snapshot.Result;

            // Nothing has loaded yet (ever), or nothing is configured to load — both are "not yet known", never a
            // guessed zero.
            _counts = snapshot.FetchedAt is null || result.RepositoryMissing
                ? PullRequestBadgeState.Unknown
                : _Counted(result);

            // Never against snapshot.FetchedAt is null: the constructor's priming call runs before the refresh
            // source's first real fetch has landed, and announcing against that placeholder would prime the seen-set
            // on nothing, so the first fetch's pre-existing review requests would all read as "just arrived".
            var arrived = _settings.UseGitHubCli && snapshot.FetchedAt is not null
                ? _Arrivals(result.ReviewRequested)
                : [];
            published = _counts with { Arrived = arrived };
            _KeepUnclaimed(arrived, result.ReviewRequested);

            // Published under the lock so two updates cannot reach the UI part in the opposite order they were
            // counted in. The UI subscriber only marshals to its own thread, so holding the lock here is brief.
            _channel.Publish(GitHubPullRequestsChannel.BadgeChanged, JsonSerializer.SerializeToElement(published, GitHubPullRequestsChannel.Json));
        }
    }

    private void _KeepUnclaimed(IReadOnlyList<GitHubPullRequest> arrived, IReadOnlyList<GitHubPullRequest> stillWaiting)
    {
        if (_claimed)
        {
            return;
        }

        foreach (var pullRequest in arrived)
        {
            _unclaimed[pullRequest.Url] = pullRequest;
        }

        var waiting = stillWaiting.Select(pullRequest => pullRequest.Url).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in _unclaimed.Keys.Where(url => !waiting.Contains(url)).ToList())
        {
            _unclaimed.Remove(gone);
        }
    }

    private PullRequestBadgeState _Counted(PullRequestFeedResult result)
    {
        var (mine, reviewRequested) = PullRequestBadgeCounts.Compute(result, _settings.IgnoredPullRequests, _settings.IgnoredRepositories);
        return new PullRequestBadgeState(mine, reviewRequested, []);
    }

    // A review request that was already waiting when the plugin first looked is not news, so the first load only
    // primes the seen-set (it has no stored one yet) and stays quiet. After that, every request that was not there
    // last time is announced once. The persisted `GitHubPullRequestsSettings.SeenReviewRequests` gate is what keeps
    // this correct across restarts.
    private IReadOnlyList<GitHubPullRequest> _Arrivals(IReadOnlyList<GitHubPullRequest> reviewRequested)
    {
        var seen = _settings.SeenReviewRequests;
        var inbox = ReviewRequestInbox.Reconcile(reviewRequested, seen ?? new HashSet<string>(StringComparer.Ordinal));
        _settings.SeenReviewRequests = inbox.Seen;

        if (seen is null || !_settings.NotifyOnReviewRequests)
        {
            return [];
        }

        var ignored = _settings.IgnoredPullRequests;
        return [.. inbox.Arrived.Where(pullRequest => !ignored.Contains(pullRequest.Url))];
    }

    public void Dispose()
    {
        _refreshSource.Updated -= _OnUpdated;
        _sessions.OutputProduced -= _OnSessionOutput;
        _signalRefresh.Dispose();
    }
}
