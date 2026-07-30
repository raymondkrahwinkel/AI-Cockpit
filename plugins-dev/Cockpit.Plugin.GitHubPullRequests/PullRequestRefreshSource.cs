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
    /// The margin <see cref="PollInterval"/> adds on top of <see cref="GitHubPrGhClient.PullRequestTtl"/> — see
    /// there for why lining the two up exactly (as this used to) defeats the point of ticking at all. Comfortably
    /// longer than a `gh` call itself ever takes (typically well under a second), so the previous cache entry has
    /// actually gone stale by the time the next tick asks, without meaningfully changing how often a real fetch
    /// happens.
    /// </summary>
    private static readonly TimeSpan PollMargin = TimeSpan.FromSeconds(30);

    /// <summary>
    /// One tick per <see cref="GitHubPrGhClient.PullRequestTtl"/> plus <see cref="PollMargin"/> — not exactly the
    /// TTL. A poll's cache lookup checks its entry's age the instant the tick fires, but that entry was written
    /// strictly *after* the previous tick, once that tick's own `gh` call actually returned. Setting this to
    /// exactly the TTL therefore never gave the entry time to age past it before the next tick asked again: the
    /// elapsed time a tick measures is always <c>PollInterval − (the previous call's own `gh` latency)</c>, which
    /// is less than the TTL for any call that takes longer than an instant — so every poll after the first was a
    /// cache replay, and <see cref="PullRequestRefreshSource.RefreshAsync"/> still stamped it with
    /// <see cref="DateTimeOffset.UtcNow"/> as if it were a brand-new fetch. The "older" marker this source exists
    /// to raise (<see cref="StaleAfter"/>) would then almost never appear, even while `gh` itself had gone unasked
    /// for far longer than three TTLs. The margin is the fix: ticking sooner than the TTL would still be a wasted
    /// `gh` call replaying the same cache (the reason to align at all), ticking by only the margin above it is
    /// enough to reliably outlive that cache entry instead of merely equaling its lifetime.
    /// </summary>
    private static readonly TimeSpan PollInterval = GitHubPrGhClient.PullRequestTtl + PollMargin;

    /// <summary>
    /// How old a fetch may be before a view is told to mark what it is showing as old. Three cache lifetimes, not
    /// three poll ticks — tied to <see cref="GitHubPrGhClient.PullRequestTtl"/> directly rather than
    /// <see cref="PollInterval"/>, so the margin above (a deliberate buffer that keeps each poll a genuine fetch)
    /// does not also stretch out how long a stalled feed takes to read as old. A single transient `gh` hiccup must
    /// not flip the marker on, but data nobody has managed to refresh across three tries — or a snapshot left over
    /// from a much earlier session — should read as old immediately.
    /// </summary>
    public static readonly TimeSpan StaleAfter = GitHubPrGhClient.PullRequestTtl * 3;

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
        _current = _ReadPersistedSnapshot() ?? PullRequestFeedSnapshot.Empty;

        // Due time zero: a fetch starts the moment the source exists, not after the first full interval — the
        // persisted/empty snapshot above is what a view shows in the meantime, never a wait.
        _timer = new Timer(_ => _ = RefreshAsync(forceRefresh: false), null, TimeSpan.Zero, pollInterval);
    }

    /// <summary>The last known answer — always available synchronously, whatever loaded it (a previous run, an earlier tick, a manual refresh).</summary>
    public PullRequestFeedSnapshot Current => _current;

    /// <summary>
    /// Reads the last persisted snapshot, treating anything that does not come back as a genuine, complete
    /// snapshot the same way as nothing having been persisted yet (AC-515). This constructor runs inside
    /// <see cref="GitHubPullRequestsPlugin.Initialize"/>, before <c>AddSettings</c>/<c>AddSideMenuSection</c>/
    /// <c>AddWidget</c> register anything — an exception escaping it is caught by <c>PluginManager.Initialize</c>,
    /// which then skips every one of this plugin's contributions, not just this source. A storage value can fail
    /// to come back usable two ways: it is not valid JSON at all (a truncated write, an older/foreign format —
    /// <see cref="JsonSerializer.Deserialize{TValue}(string, JsonSerializerOptions)"/> throws), or it deserializes
    /// without throwing but into a shape this record does not actually carry (e.g. a value written under this key
    /// by something else, or a schema this snapshot no longer matches) — <see cref="PullRequestFeedSnapshot.Result"/>
    /// is a required, non-nullable reference, but a JSON object missing that property still deserializes to a
    /// snapshot with a null one, since deserialization does not enforce non-null reference members. Either way
    /// nothing usable was found, so the caller falls back to <see cref="PullRequestFeedSnapshot.Empty"/>.
    /// <para>
    /// The same gap exists one level deeper: a stored <c>{"Result":{}}</c> deserializes to a non-null
    /// <see cref="PullRequestFeedResult"/> whose <see cref="PullRequestFeedResult.PullRequests"/> and
    /// <see cref="PullRequestFeedResult.ReviewRequested"/> are themselves null — same reason, System.Text.Json
    /// does not enforce non-null reference members on a positional record's parameters either. That snapshot
    /// would pass a bare <c>Result: not null</c> check and reach a view — <see cref="GitHubPullRequestsWidget"/>'s
    /// <c>result.ReviewRequested.Select(...)</c> throws a <see cref="NullReferenceException"/> rendering it — so
    /// both collections are checked here too, not just their container.
    /// </para>
    /// </summary>
    private PullRequestFeedSnapshot? _ReadPersistedSnapshot()
    {
        try
        {
            return _storage.Get<PullRequestFeedSnapshot>(StorageKey) is
                { Result: { PullRequests: not null, ReviewRequested: not null } } snapshot
                ? snapshot
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

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

        Updated?.Invoke(this, _current);
        return true;
    }

    /// <summary>
    /// Does not wait for a refresh that is mid-flight at the moment this runs (a poll tick or the settings-saved
    /// callback landing the instant the plugin unloads) — <see cref="ICockpitPlugin.Dispose"/> is synchronous, and
    /// blocking it on an in-progress `gh` call would hang the app's own shutdown on network/process latency this
    /// class does not control. Instead it makes finishing safe: <see cref="RefreshAsync"/> catches
    /// <see cref="ObjectDisposedException"/> around every touch of <see cref="_refreshGate"/>, so a call already
    /// running when this executes still completes (and still updates <see cref="_current"/>/<see cref="LastError"/>
    /// and raises <see cref="Updated"/>) without a disposed gate throwing out of it.
    /// </summary>
    public void Dispose()
    {
        _timer.Dispose();
        _refreshGate.Dispose();
    }
}
