using System.Collections.Concurrent;
using System.Text.Json;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.GitHubPullRequests.Tests;

/// <summary>
/// AC-515: refreshing has to run independent of any view, never make a caller wait on a miss, survive a restart
/// with the previous list marked old, and not cost more `gh` calls per unit time than before. Every test here
/// drives <see cref="PullRequestRefreshSource"/> directly — no <see cref="GitHubPullRequestsWidget"/> or the
/// AC-517 side-menu badge involved — via a fake load function (no `gh`, no network), which is exactly what
/// acceptance criterion 2 asks for ("aantoonbaar met een test op de verversingsbron, niet op een control").
/// </summary>
public class PullRequestRefreshSourceTests
{
    private static readonly GitHubPullRequest SamplePullRequest = new(1, "Fix the thing", "https://github.com/octocat/hello-world/pull/1", null, "octocat/hello-world", "octocat");

    [Fact]
    public async Task Refresh_RunsOnItsOwnTimer_WithNoViewEverAttached()
    {
        var calls = 0;
        var source = new PullRequestRefreshSource(
            new InMemoryStorage(),
            (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(new PullRequestFeedResult([], [], RepositoryMissing: false));
            },
            pollInterval: TimeSpan.FromMilliseconds(30));

        // Nothing here ever builds a Widget or a badge, or attaches anything to a visual tree — the background
        // poll firing more than once is entirely the source's own doing.
        var sawMultipleTicks = await _WaitUntilAsync(() => Volatile.Read(ref calls) >= 3, TimeSpan.FromSeconds(2));

        source.Dispose();

        Assert.True(sawMultipleTicks, $"expected at least 3 background ticks with no view attached, saw {calls}");
    }

    [Fact]
    public async Task ColdStart_ShowsThePersistedSnapshotBeforeAnyFetchCompletes()
    {
        var storage = new InMemoryStorage();
        var oldSnapshot = new PullRequestFeedSnapshot(
            new PullRequestFeedResult([SamplePullRequest], [], RepositoryMissing: false),
            DateTimeOffset.UtcNow - PullRequestRefreshSource.StaleAfter - TimeSpan.FromMinutes(1));
        storage.Set("refreshSourceSnapshot", oldSnapshot);

        var release = new TaskCompletionSource<PullRequestFeedResult>();
        var source = new PullRequestRefreshSource(storage, (_, _) => release.Task, pollInterval: TimeSpan.FromMinutes(10));

        // The very first fetch is still pending (release.Task has not completed) — Current must already be the
        // restart-time list, not empty and not a wait.
        var beforeFetch = source.Current;

        var freshPullRequest = SamplePullRequest with { Title = "Fix the other thing" };
        release.SetResult(new PullRequestFeedResult([freshPullRequest], [], RepositoryMissing: false));

        // Wait for the write to storage as well, not just for Current: the source assigns _current and only then
        // calls _storage.Set, so a wait on Current alone can return inside that window and read the pre-fetch
        // snapshot back out — which is what made this test flaky on a loaded runner.
        await _WaitUntilAsync(
            () => source.Current.Result.PullRequests.Count > 0
                  && source.Current.Result.PullRequests[0].Title == freshPullRequest.Title
                  && storage.Get<PullRequestFeedSnapshot>("refreshSourceSnapshot")?.Result.PullRequests is [{ Title: "Fix the other thing" }, ..],
            TimeSpan.FromSeconds(2));

        var afterFetch = source.Current;
        var persisted = storage.Get<PullRequestFeedSnapshot>("refreshSourceSnapshot");

        source.Dispose();

        Assert.Equal(SamplePullRequest.Title, beforeFetch.Result.PullRequests[0].Title);
        Assert.True(DateTimeOffset.UtcNow - beforeFetch.FetchedAt!.Value > PullRequestRefreshSource.StaleAfter, "the pre-fetch snapshot has to be the old, restart-time one");
        Assert.Equal(freshPullRequest.Title, afterFetch.Result.PullRequests[0].Title);
        Assert.True(DateTimeOffset.UtcNow - afterFetch.FetchedAt!.Value < TimeSpan.FromSeconds(5), "the post-fetch snapshot has to be freshly timestamped");
        Assert.Equal(freshPullRequest.Title, persisted?.Result.PullRequests[0].Title);
    }

    [Fact]
    public async Task OverlappingRefreshCalls_CollapseIntoOneLoad()
    {
        var calls = 0;
        var firstCallSeen = new TaskCompletionSource();
        var release = new TaskCompletionSource<PullRequestFeedResult>();
        var emptyResult = new PullRequestFeedResult([], [], RepositoryMissing: false);

        // The constructor's own due-time-zero tick fires as soon as construction returns and would otherwise race
        // the three overlapping calls below for who counts as "the winner" — the first call answers instantly and
        // is drained (awaited via firstCallSeen) before the overlap is measured, so what is under test is the three
        // calls issued here, not an accident of thread-pool scheduling.
        var source = new PullRequestRefreshSource(
            new InMemoryStorage(),
            (_, _) =>
            {
                var n = Interlocked.Increment(ref calls);
                if (n == 1)
                {
                    firstCallSeen.TrySetResult();
                    return Task.FromResult(emptyResult);
                }

                return release.Task;
            },
            pollInterval: TimeSpan.FromMinutes(10));

        await firstCallSeen.Task;
        await Task.Delay(30); // lets the first RefreshAsync finish releasing the gate before the overlap starts

        var overlapping = new[]
        {
            source.RefreshAsync(forceRefresh: true),
            source.RefreshAsync(forceRefresh: true),
            source.RefreshAsync(forceRefresh: true),
        };

        release.SetResult(emptyResult);
        var ran = await Task.WhenAll(overlapping);

        source.Dispose();

        Assert.Equal(2, calls); // the initial tick, plus exactly one of the three overlapping callers
        Assert.Equal(2, ran.Count(x => !x));
    }

    /// <summary>
    /// AC-515 blocker 2: a snapshot that is not valid JSON at all — a truncated write (see blocker 1) or a leftover
    /// from an incompatible earlier build — must read as "nothing persisted yet", the same as a fresh install,
    /// rather than throw out of the constructor. This runs inside <c>GitHubPullRequestsPlugin.Initialize</c>, before
    /// <c>AddSettings</c>/<c>AddSideMenuSection</c>/<c>AddWidget</c> register anything — an unguarded throw here
    /// used to make <c>PluginManager.Initialize</c> skip every one of this plugin's contributions over one bad key.
    /// <see cref="JsonBackedStorage"/> (unlike <see cref="InMemoryStorage"/>) round-trips through
    /// <see cref="System.Text.Json.JsonSerializer"/> like the host's real <c>PluginStorage</c> does, so this
    /// actually drives the deserialize path the bug lives in.
    /// </summary>
    [Fact]
    public void ColdStart_WithUnparsableStoredJson_FallsBackToEmpty_InsteadOfThrowing()
    {
        var storage = new JsonBackedStorage();
        storage.SeedRaw("refreshSourceSnapshot", "not json at all");

        var source = new PullRequestRefreshSource(storage, (_, _) => Task.FromResult(new PullRequestFeedResult([], [], RepositoryMissing: false)), pollInterval: TimeSpan.FromMinutes(10));

        var current = source.Current;
        source.Dispose();

        Assert.Empty(current.Result.PullRequests);
        Assert.False(current.Result.RepositoryMissing);
        Assert.Null(current.FetchedAt);
    }

    /// <summary>
    /// AC-515 blocker 2's other failure shape: valid JSON that simply is not this record's shape (e.g. a value
    /// written under this key by something unrelated). <see cref="System.Text.Json.JsonSerializer"/> does not
    /// throw for this — <see cref="PullRequestFeedSnapshot.Result"/> is a required, non-nullable reference, but a
    /// missing JSON property still deserializes to a snapshot whose <c>Result</c> is null, since deserialization
    /// does not enforce non-null reference members. A bare <c>?? Empty</c> on the constructor's read would miss
    /// this: the deserialized object is not null, only its <c>Result</c> is — so <see cref="PullRequestRefreshSource"/>
    /// must reject it explicitly rather than merely catch an exception that never comes.
    /// </summary>
    [Fact]
    public void ColdStart_WithWrongShapedJson_FallsBackToEmpty_InsteadOfCarryingANullResult()
    {
        var storage = new JsonBackedStorage();
        storage.SeedRaw("refreshSourceSnapshot", """{"totally":"unrelated","shape":true}""");

        var source = new PullRequestRefreshSource(storage, (_, _) => Task.FromResult(new PullRequestFeedResult([], [], RepositoryMissing: false)), pollInterval: TimeSpan.FromMinutes(10));

        var current = source.Current;
        source.Dispose();

        Assert.NotNull(current.Result);
        Assert.Empty(current.Result.PullRequests);
        Assert.Null(current.FetchedAt);
    }

    /// <summary>
    /// A confirming review's follow-up on the same class of bug, one level deeper: a stored <c>{"Result":{}}</c>
    /// deserializes to a non-null <see cref="PullRequestFeedResult"/> whose <c>PullRequests</c>/<c>ReviewRequested</c>
    /// are themselves null — <see cref="System.Text.Json.JsonSerializer"/> enforces non-null reference members on
    /// neither the record nor its positional parameters. A bare <c>Result: not null</c> check (the fix for the
    /// blocker above) lets this one through; <c>GitHubPullRequestsWidget._ApplySnapshot</c>'s
    /// <c>result.ReviewRequested.Select(...)</c> would throw a <see cref="NullReferenceException"/> rendering it.
    /// </summary>
    [Fact]
    public void ColdStart_WithNullCollectionsInsideResult_FallsBackToEmpty_InsteadOfCarryingNullLists()
    {
        var storage = new JsonBackedStorage();
        storage.SeedRaw("refreshSourceSnapshot", """{"Result":{}}""");

        var source = new PullRequestRefreshSource(storage, (_, _) => Task.FromResult(new PullRequestFeedResult([], [], RepositoryMissing: false)), pollInterval: TimeSpan.FromMinutes(10));

        var current = source.Current;
        source.Dispose();

        Assert.NotNull(current.Result.PullRequests);
        Assert.NotNull(current.Result.ReviewRequested);
        Assert.Empty(current.Result.PullRequests);
        Assert.Null(current.FetchedAt);
    }

    [Fact]
    public void StaleAfter_IsThreeTimesTheGhClientTtl_NotARoundedNumber()
    {
        // Asserted against the constant itself, not a literal like TimeSpan.FromMinutes(15) — a change to the
        // client's own TTL must not silently desynchronise the marker's threshold from what the doc comment claims.
        Assert.Equal(GitHubPrGhClient.PullRequestTtl * 3, PullRequestRefreshSource.StaleAfter);
    }

    /// <summary>
    /// Adversarial-review defect: <c>Dispose()</c> tore down the gate a still-running <see cref="PullRequestRefreshSource.RefreshAsync"/>
    /// call was about to release into. This reproduces the exact shape — a call holding the gate and mid-`_load`
    /// when <c>Dispose()</c> runs on top of it, then the load completing afterwards — by draining the constructor's
    /// own due-time-zero tick first (same technique as <see cref="OverlappingRefreshCalls_CollapseIntoOneLoad"/>) so
    /// the call under test is the only one holding the gate, then issuing it directly to get a real <see cref="Task{TResult}"/>
    /// handle a fire-and-forget timer callback never gives the production code. Before the fix this call's task
    /// faulted with <see cref="ObjectDisposedException"/> once <c>release</c> completed — unobserved in production,
    /// since every real caller is `_ = RefreshAsync(...)`.
    /// </summary>
    [Fact]
    public async Task Dispose_WhileARefreshIsInFlight_DoesNotThrowFromTheGateItDisposes()
    {
        var initialTickSeen = new TaskCompletionSource();
        var emptyResult = new PullRequestFeedResult([], [], RepositoryMissing: false);
        var release = new TaskCompletionSource<PullRequestFeedResult>();
        var calls = 0;

        var source = new PullRequestRefreshSource(
            new InMemoryStorage(),
            (_, _) =>
            {
                var n = Interlocked.Increment(ref calls);
                if (n == 1)
                {
                    initialTickSeen.TrySetResult();
                    return Task.FromResult(emptyResult);
                }

                return release.Task;
            },
            pollInterval: TimeSpan.FromMinutes(10));

        await initialTickSeen.Task;
        await Task.Delay(30); // lets the first RefreshAsync finish releasing the gate before the call under test starts

        // Holds the gate and is suspended awaiting `_load` (release.Task, still pending) the moment Dispose runs —
        // the callback-still-running-at-unload shape the review flagged.
        var inFlight = source.RefreshAsync(forceRefresh: true);

        source.Dispose();
        release.SetResult(emptyResult);

        var ran = await inFlight;

        Assert.True(ran, "the in-flight call actually ran the load and should still report that, not fault");
    }

    /// <summary>
    /// The other half of the same defect: a call that had not even reached the gate yet when <c>Dispose()</c> ran —
    /// <see cref="SemaphoreSlim.WaitAsync(int)"/> itself throws <see cref="ObjectDisposedException"/> unconditionally
    /// once the semaphore is disposed, regardless of its count. Before the fix this propagated straight out of
    /// <see cref="PullRequestRefreshSource.RefreshAsync"/>.
    /// </summary>
    [Fact]
    public async Task RefreshAsync_CalledAfterDispose_IsGatedOutInsteadOfThrowing()
    {
        var source = new PullRequestRefreshSource(
            new InMemoryStorage(),
            (_, _) => Task.FromResult(new PullRequestFeedResult([], [], RepositoryMissing: false)),
            pollInterval: TimeSpan.FromMinutes(10));

        await Task.Delay(50); // lets the constructor's own due-time-zero tick finish and release the gate
        source.Dispose();

        var ranAfterDispose = await source.RefreshAsync(forceRefresh: true);

        Assert.False(ranAfterDispose, "a call arriving after Dispose must be gated out like any other, not throw");
    }

    [Fact]
    public async Task AFailedFetch_StillRaisesUpdated_SoAFirstEverAttemptIsNotSilent()
    {
        var storage = new InMemoryStorage();

        // The constructor's due-time-zero tick fetches straight away, so a handler attached on the line after it can
        // already be too late: the fetch fails, Updated fires with nothing listening, and the wait below then just
        // runs its two seconds out and reports a null that never had a chance. Holding the fetch until the handler
        // is actually on makes the ordering this test's own rather than the thread pool's. More time would not have
        // helped — the event it is waiting for is already gone.
        var subscribed = new TaskCompletionSource();
        var source = new PullRequestRefreshSource(
            storage,
            async (_, _) =>
            {
                await subscribed.Task;
                throw new InvalidOperationException("gh not installed");
            },
            pollInterval: TimeSpan.FromMinutes(10));

        PullRequestFeedSnapshot? received = null;
        source.Updated += (_, snapshot) => received = snapshot;
        subscribed.SetResult();

        var raised = await _WaitUntilAsync(() => received is not null, TimeSpan.FromSeconds(2));

        source.Dispose();

        Assert.True(raised, "a failed fetch must still raise Updated, so a first-ever attempt is not silent");
        Assert.NotNull(received);
        Assert.Null(received!.FetchedAt);
        Assert.Null(storage.Get<PullRequestFeedSnapshot>("refreshSourceSnapshot"));
    }

    [Fact]
    public async Task ExplicitRefresh_ThatRan_ReportsItsOwnFailure()
    {
        var source = new PullRequestRefreshSource(
            new InMemoryStorage(),
            (_, _) => Task.FromException<PullRequestFeedResult>(new InvalidOperationException("gh not installed")),
            pollInterval: TimeSpan.FromMinutes(10));

        // Give the constructor's own immediate tick a moment to run and fail before this call lands, so this call
        // is the one under test rather than racing the first tick for who actually fetches.
        await Task.Delay(50);
        var ran = await source.RefreshAsync(forceRefresh: true);

        source.Dispose();

        Assert.True(ran);
        Assert.NotNull(source.LastError);
        Assert.Equal("gh not installed", source.LastError!.Message);
    }

    private static async Task<bool> _WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(10);
        }

        return condition();
    }

    /// <summary>Concurrent because a background poll writes here while a test's wait loop reads — a plain dictionary throws or returns garbage on that overlap.</summary>
    private sealed class InMemoryStorage : IPluginStorage
    {
        private readonly ConcurrentDictionary<string, object?> _values = new();

        public T? Get<T>(string key) => _values.TryGetValue(key, out var value) ? (T?)value : default;

        public void Set<T>(string key, T value) => _values[key] = value;
    }

    /// <summary>
    /// Unlike <see cref="InMemoryStorage"/>, stores values as the raw JSON strings the host's real
    /// <c>PluginStorage</c> does — needed for the malformed/wrong-shape tests above, which are only real bugs on
    /// the JSON round trip; <see cref="InMemoryStorage"/> keeps the live object and would never exercise
    /// <see cref="JsonSerializer.Deserialize{TValue}(string, JsonSerializerOptions)"/> at all.
    /// </summary>
    private sealed class JsonBackedStorage : IPluginStorage
    {
        private readonly Dictionary<string, string> _values = [];

        public void SeedRaw(string key, string rawJson) => _values[key] = rawJson;

        public T? Get<T>(string key) => _values.TryGetValue(key, out var json) ? JsonSerializer.Deserialize<T>(json) : default;

        public void Set<T>(string key, T value) => _values[key] = JsonSerializer.Serialize(value);
    }
}
