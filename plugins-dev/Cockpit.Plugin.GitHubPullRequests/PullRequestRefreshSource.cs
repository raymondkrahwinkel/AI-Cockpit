using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.GitHubPullRequests;

/// <summary>
/// The one place that decides "is the pull-request list fresh enough" for this plugin instance (AC-515). Built once
/// in <see cref="GitHubPullRequestsPlugin.Initialize"/> and disposed when the plugin unloads — not per control, not
/// per view. The side section and every dashboard widget instance subscribe to <see cref="Updated"/> and read
/// <see cref="Current"/> instead of running their own timer or awaiting a fresh `gh` call before they can draw
/// anything; a future consumer (the AC-517 side-menu badge) attaches the same way.
/// <para>
/// There was already a cache and already a timer before this existed. What made the plugin feel like it "only loads
/// while you're looking at it" was that both lived on the control: the timer ran only while a view was attached to
/// the visual tree (a widget scrolled out of sight never polled), and a cache miss made whoever asked wait for `gh`
/// to answer. This fixes both by construction — the poll here runs independent of any view, and <see cref="Current"/>
/// is always the last known answer, never a wait.
/// </para>
/// </summary>
internal sealed class PullRequestRefreshSource : IDisposable
{
    /// <summary>
    /// Aligned with <see cref="GitHubPrGhClient.PullRequestTtl"/> on purpose: ticking sooner would ask again before
    /// an entry can even be stale (a wasted `gh` call replaying the same cache), ticking much later would leave the
    /// list looking unchanged longer than the client's own cache already allows.
    /// </summary>
    private static readonly TimeSpan PollInterval = GitHubPrGhClient.PullRequestTtl;

    /// <summary>
    /// How old a fetch may be before a view is told to mark what it is showing as old. Three missed polls, not one:
    /// a single transient `gh` hiccup must not flip the marker on, but data nobody has managed to refresh across
    /// three tries — or a snapshot left over from a much earlier session — should read as old immediately.
    /// </summary>
    public static readonly TimeSpan StaleAfter = PollInterval * 3;

    private const string StorageKey = "refreshSourceSnapshot";

    private readonly IPluginStorage _storage;
    private readonly Func<bool, CancellationToken, Task<PullRequestFeedResult>> _load;
    private readonly Timer _timer;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private PullRequestFeedSnapshot _current;

    /// <summary>Raised on the thread the fetch completed on (a thread-pool timer callback) — a subscriber that touches UI must marshal itself.</summary>
    public event EventHandler<PullRequestFeedSnapshot>? Updated;

    public PullRequestRefreshSource(ICockpitHost host, GitHubPullRequestsSettings settings)
        : this(host.Storage, (forceRefresh, cancellationToken) => new PullRequestFeed().LoadAsync(settings, forceRefresh, cancellationToken), PollInterval)
    {
        // A settings change (owner, watched repos, the CLI toggle) can change what the next fetch should even ask
        // for — reload once, here, rather than every subscribed view repeating the same reload for itself.
        host.OnSettingsSaved(() => _ = RefreshAsync(forceRefresh: true));
    }

    /// <summary>The seam a test drives directly: a fake load function (no `gh`, no network) and a storage double, so the polling/persistence/staleness behaviour is provable without shelling out.</summary>
    internal PullRequestRefreshSource(IPluginStorage storage, Func<bool, CancellationToken, Task<PullRequestFeedResult>> load, TimeSpan pollInterval)
    {
        _storage = storage;
        _load = load;
        _current = _storage.Get<PullRequestFeedSnapshot>(StorageKey) ?? PullRequestFeedSnapshot.Empty;

        // Due time zero: a fetch starts the moment the source exists, not after the first full interval — the
        // persisted/empty snapshot above is what a view shows in the meantime, never a wait.
        _timer = new Timer(_ => _ = RefreshAsync(forceRefresh: false), null, TimeSpan.Zero, pollInterval);
    }

    /// <summary>The last known answer — always available synchronously, whatever loaded it (a previous run, an earlier tick, a manual refresh).</summary>
    public PullRequestFeedSnapshot Current => _current;

    /// <summary>What the caller's own attempt failed with, if <see cref="RefreshAsync"/> returned <see langword="true"/> and it did not succeed. Never set by an attempt a caller was gated out of — see <see cref="RefreshAsync"/>.</summary>
    public Exception? LastError { get; private set; }

    /// <summary>
    /// Runs one load and publishes it. Overlapping callers (a poll tick, a session-signal debounce and a manual
    /// click landing together) collapse into one `gh` round trip instead of stacking one per caller: whichever
    /// arrives first fetches; the rest return <see langword="false"/> immediately, gated out rather than queued —
    /// they are satisfied by the <see cref="Updated"/> that fetch is about to raise anyway, and must not read
    /// <see cref="LastError"/> as if it were their own attempt's outcome.
    /// </summary>
    /// <returns><see langword="true"/> if this call actually ran the load (whether it then succeeded or failed), <see langword="false"/> if another call was already in flight.</returns>
    public async Task<bool> RefreshAsync(bool forceRefresh)
    {
        if (!await _refreshGate.WaitAsync(0))
        {
            return false;
        }

        try
        {
            var result = await _load(forceRefresh, CancellationToken.None);
            _current = new PullRequestFeedSnapshot(result, DateTimeOffset.UtcNow);
            _storage.Set(StorageKey, _current);
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
            _refreshGate.Release();
        }

        Updated?.Invoke(this, _current);
        return true;
    }

    public void Dispose()
    {
        _timer.Dispose();
        _refreshGate.Dispose();
    }
}
