using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.GitHubPullRequests;

// The one place that decides "is the pull-request list fresh enough" for this plugin instance (AC-515). Built once
// in `GitHubPullRequestsPlugin.Initialize` and disposed when the plugin unloads — not per control, not
// per view. Every dashboard widget instance and the side-menu badge (AC-517) subscribe to `Updated`
// and read `Current` instead of running their own timer or awaiting a fresh `gh` call before they can
// show anything.
//
// There was already a cache and already a timer before this existed. What made the plugin feel like it "only loads
// while you're looking at it" was that both lived on the control: the timer ran only while a view was attached to
// the visual tree (a widget scrolled out of sight never polled), and a cache miss made whoever asked wait for `gh`
// to answer. This fixes both by construction — the poll here runs independent of any view, and `Current`
// is always the last known answer, never a wait.
internal sealed class PullRequestRefreshSource : IDisposable
{
    // The margin `PollInterval` adds on top of `GitHubPrGhClient.PullRequestTtl` — see
    // there for why lining the two up exactly (as this used to) defeats the point of ticking at all. Comfortably
    // longer than a `gh` call itself ever takes (typically well under a second), so the previous cache entry has
    // actually gone stale by the time the next tick asks, without meaningfully changing how often a real fetch
    // happens.
    private static readonly TimeSpan PollMargin = TimeSpan.FromSeconds(30);

    // One tick per `GitHubPrGhClient.PullRequestTtl` plus `PollMargin` — not exactly the
    // TTL. A poll's cache lookup checks its entry's age the instant the tick fires, but that entry was written
    // strictly *after* the previous tick, once that tick's own `gh` call actually returned. Setting this to
    // exactly the TTL therefore never gave the entry time to age past it before the next tick asked again: the
    // elapsed time a tick measures is always `PollInterval − (the previous call's own `gh` latency)`, which
    // is less than the TTL for any call that takes longer than an instant — so every poll after the first was a
    // cache replay, and `PullRequestRefreshSource.RefreshAsync` still stamped it with
    // `DateTimeOffset.UtcNow` as if it were a brand-new fetch. The "older" marker this source exists
    // to raise (`StaleAfter`) would then almost never appear, even while `gh` itself had gone unasked
    // for far longer than three TTLs. The margin is the fix: ticking sooner than the TTL would still be a wasted
    // `gh` call replaying the same cache (the reason to align at all), ticking by only the margin above it is
    // enough to reliably outlive that cache entry instead of merely equaling its lifetime.
    private static readonly TimeSpan PollInterval = GitHubPrGhClient.PullRequestTtl + PollMargin;

    // How old a fetch may be before a view is told to mark what it is showing as old. Three cache lifetimes, not
    // three poll ticks — tied to `GitHubPrGhClient.PullRequestTtl` directly rather than
    // `PollInterval`, so the margin above (a deliberate buffer that keeps each poll a genuine fetch)
    // does not also stretch out how long a stalled feed takes to read as old. A single transient `gh` hiccup must
    // not flip the marker on, but data nobody has managed to refresh across three tries — or a snapshot left over
    // from a much earlier session — should read as old immediately.
    public static readonly TimeSpan StaleAfter = GitHubPrGhClient.PullRequestTtl * 3;

    private const string StorageKey = "refreshSourceSnapshot";

    // The restart cache draws lists and badges before the first fetch; PR bodies arrive only with a live fetch.
    private sealed record PersistedSnapshot(PersistedResult Result, DateTimeOffset? FetchedAt)
    {
        public PullRequestFeedSnapshot ToRuntime() => new(Result.ToRuntime(), FetchedAt);

        public static PersistedSnapshot From(PullRequestFeedSnapshot snapshot) =>
            new(PersistedResult.From(snapshot.Result), snapshot.FetchedAt);
    }

    private sealed record PersistedResult(
        IReadOnlyList<PersistedPullRequest> PullRequests,
        IReadOnlyList<PersistedPullRequest> ReviewRequested,
        bool RepositoryMissing)
    {
        public PullRequestFeedResult ToRuntime() =>
            new(PullRequests.Select(pullRequest => pullRequest.ToRuntime()).ToArray(),
                ReviewRequested.Select(pullRequest => pullRequest.ToRuntime()).ToArray(),
                RepositoryMissing);

        public static PersistedResult From(PullRequestFeedResult result) =>
            new(result.PullRequests.Select(PersistedPullRequest.From).ToArray(),
                result.ReviewRequested.Select(PersistedPullRequest.From).ToArray(),
                result.RepositoryMissing);
    }

    private sealed record PersistedPullRequest(
        int Number,
        string Title,
        string Url,
        string Repository,
        string Author,
        DateTimeOffset? UpdatedAt)
    {
        public GitHubPullRequest ToRuntime() => new(Number, Title, Url, Body: null, Repository, Author, UpdatedAt);

        public static PersistedPullRequest From(GitHubPullRequest pullRequest) =>
            new(pullRequest.Number, pullRequest.Title, pullRequest.Url, pullRequest.Repository, pullRequest.Author, pullRequest.UpdatedAt);
    }

    private readonly IPluginStorage _storage;
    private readonly Func<bool, CancellationToken, Task<PullRequestFeedResult>> _load;
    private readonly Timer _timer;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private PullRequestFeedSnapshot _current;

    // Raised on the thread the fetch completed on (a thread-pool timer callback) — a subscriber that touches UI must marshal itself.
    public event EventHandler<PullRequestFeedSnapshot>? Updated;

    public PullRequestRefreshSource(ICockpitHost host, GitHubPullRequestsSettings settings)
        : this(host.Storage, (forceRefresh, cancellationToken) => new PullRequestFeed().LoadAsync(settings, forceRefresh, cancellationToken), PollInterval)
    {
        // A settings change (owner, watched repos, the CLI toggle) can change what the next fetch should even ask
        // for — reload once, here, rather than every subscribed view repeating the same reload for itself.
        host.OnSettingsSaved(() => _ = RefreshAsync(forceRefresh: true));
    }

    // The seam a test drives directly: a fake load function (no `gh`, no network) and a storage double, so the polling/persistence/staleness behaviour is provable without shelling out.
    internal PullRequestRefreshSource(IPluginStorage storage, Func<bool, CancellationToken, Task<PullRequestFeedResult>> load, TimeSpan pollInterval)
    {
        _storage = storage;
        _load = load;
        _current = _ReadPersistedSnapshot() ?? PullRequestFeedSnapshot.Empty;

        // Due time zero: a fetch starts the moment the source exists, not after the first full interval — the
        // persisted/empty snapshot above is what a view shows in the meantime, never a wait.
        _timer = new Timer(_ => _ = RefreshAsync(forceRefresh: false), null, TimeSpan.Zero, pollInterval);
    }

    // The last known answer — always available synchronously, whatever loaded it (a previous run, an earlier tick, a manual refresh).
    public PullRequestFeedSnapshot Current => _current;

    // Reads the last persisted snapshot, treating anything that does not come back as a genuine, complete
    // snapshot the same way as nothing having been persisted yet (AC-515). This constructor runs inside
    // `GitHubPullRequestsPlugin.Initialize`, before `AddSettings`/`AddSideMenuSection`/
    // `AddWidget` register anything — an exception escaping it is caught by `PluginManager.Initialize`,
    // which then skips every one of this plugin's contributions, not just this source. A storage value can fail
    // to come back usable two ways: it is not valid JSON at all (a truncated write, an older/foreign format —
    // `JsonSerializer.Deserialize{TValue}(string, JsonSerializerOptions)` throws), or it deserializes
    // without throwing but into a shape this record does not actually carry (e.g. a value written under this key
    // by something else, or a schema this snapshot no longer matches) — `PullRequestFeedSnapshot.Result`
    // is a required, non-nullable reference, but a JSON object missing that property still deserializes to a
    // snapshot with a null one, since deserialization does not enforce non-null reference members. Either way
    // nothing usable was found, so the caller falls back to `PullRequestFeedSnapshot.Empty`.
    //
    // The same gap exists one level deeper: a stored `{"Result":{}}` deserializes to a non-null
    // `PullRequestFeedResult` whose `PullRequestFeedResult.PullRequests` and
    // `PullRequestFeedResult.ReviewRequested` are themselves null — same reason, System.Text.Json
    // does not enforce non-null reference members on a positional record's parameters either. That snapshot
    // would pass a bare `Result: not null` check and reach a view — `GitHubPullRequestsWidget`'s
    // `result.ReviewRequested.Select(...)` throws a `NullReferenceException` rendering it — so
    // both collections are checked here too, not just their container.
    private PullRequestFeedSnapshot? _ReadPersistedSnapshot()
    {
        try
        {
            return _storage.Get<PersistedSnapshot>(StorageKey) is
                { Result: { PullRequests: not null, ReviewRequested: not null } } snapshot
                ? snapshot.ToRuntime()
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // What the caller's own attempt failed with, if `RefreshAsync` returned `true` and it did not succeed. Never set by an attempt a caller was gated out of — see `RefreshAsync`.
    public Exception? LastError { get; private set; }

    // Runs one load and publishes it. Overlapping callers (a poll tick, a session-signal debounce and a manual
    // click landing together) collapse into one `gh` round trip instead of stacking one per caller: whichever
    // arrives first fetches; the rest return `false` immediately, gated out rather than queued —
    // they are satisfied by the `Updated` that fetch is about to raise anyway, and must not read
    // `LastError` as if it were their own attempt's outcome.
    // `true` if this call actually ran the load (whether it then succeeded or failed), `false` if another call was already in flight.
    public async Task<bool> RefreshAsync(bool forceRefresh)
    {
        bool acquired;
        try
        {
            acquired = await _refreshGate.WaitAsync(0);
        }
        catch (ObjectDisposedException)
        {
            // Dispose() (the plugin unloading) ran between this call being kicked off — a poll tick or the
            // settings-saved callback, both fire-and-forget, neither one this class can unsubscribe (see the
            // constructor) — and it reaching the gate. Nothing is left to refresh for; treat it exactly like
            // being gated out rather than let this surface as an unobserved exception from a caller nobody awaits.
            return false;
        }

        if (!acquired)
        {
            return false;
        }

        try
        {
            var result = await _load(forceRefresh, CancellationToken.None);
            _current = new PullRequestFeedSnapshot(result, DateTimeOffset.UtcNow);
            _storage.Set(StorageKey, PersistedSnapshot.From(_current));
            LastError = null;
        }
        catch (Exception exception)
        {
            // A failed background poll keeps the last known snapshot rather than clearing it — a view showing an
            // older list is more useful than one showing nothing, and the next tick tries again. Still raised below:
            // a subscriber that has never seen a successful fetch (a first-run failure) needs to hear that an
            // attempt happened at all, or it would sit on an empty state that reads as "loading" forever. An
            // explicit (manual/settings) caller reads LastError to report the failure; a quiet poll does not.
            LastError = exception;
        }
        finally
        {
            try
            {
                _refreshGate.Release();
            }
            catch (ObjectDisposedException)
            {
                // Dispose() ran while `_load` above was still in flight — the callback that started this call had
                // already begun before the plugin was torn down. The gate it would release into is gone and
                // nobody is left waiting on it; swallow rather than let this escape from the fire-and-forget
                // caller (the timer tick or the settings-saved handler) that kicked it off in the first place.
            }
        }

        // Deliberately re-reads the field instead of publishing the snapshot this call's own load produced: a
        // publisher overtaken between the release above and this line must raise the newest snapshot, never its
        // own older one — the badge updater turns an older one arriving late into a repeated toast (AC-1250).
        Updated?.Invoke(this, _current);
        return true;
    }

    // Does not wait for a refresh that is mid-flight at the moment this runs (a poll tick or the settings-saved
    // callback landing the instant the plugin unloads) — `ICockpitPlugin.Dispose` is synchronous, and
    // blocking it on an in-progress `gh` call would hang the app's own shutdown on network/process latency this
    // class does not control. Instead it makes finishing safe: `RefreshAsync` catches
    // `ObjectDisposedException` around every touch of `_refreshGate`, so a call already
    // running when this executes still completes (and still updates `_current`/`LastError`
    // and raises `Updated`) without a disposed gate throwing out of it.
    public void Dispose()
    {
        _timer.Dispose();
        _refreshGate.Dispose();
    }
}
