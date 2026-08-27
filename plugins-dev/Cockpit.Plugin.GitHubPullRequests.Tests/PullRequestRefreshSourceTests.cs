namespace Cockpit.Plugin.GitHubPullRequests.Tests;

// AC-515: refreshing has to run independent of any view, never make a caller wait on a miss, survive a restart
// with the previous list marked old, and not cost more `gh` calls per unit time than before. Every test here
// drives `PullRequestRefreshSource` directly — no `GitHubPullRequestsWidget` or the
// AC-517 side-menu badge involved — via a fake load function (no `gh`, no network), which is exactly what
// acceptance criterion 2 asks for ("aantoonbaar met een test op de verversingsbron, niet op een control").
public class PullRequestRefreshSourceTests
{
    private static readonly GitHubPullRequest SamplePullRequest = new(1, "Fix the thing", "https://github.com/octocat/hello-world/pull/1", null, "octocat/hello-world", "octocat");

    [Fact]
    public async Task Refresh_RunsOnItsOwnTimer_WithNoViewEverAttached()
    {
        var calls = 0;
        var source = new PullRequestRefreshSource(
            new InMemoryPluginStorage(),
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
        var storage = new InMemoryPluginStorage();
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
    public async Task RestartSnapshot_PersistsRenderableFieldsWithoutBody()
    {
        var storage = new InMemoryPluginStorage();
        var legacyPullRequest = SamplePullRequest with { Body = "legacy body" };
        storage.Set(
            "refreshSourceSnapshot",
            new PullRequestFeedSnapshot(
                new PullRequestFeedResult([legacyPullRequest], [], RepositoryMissing: false),
                DateTimeOffset.UtcNow));

        var freshPullRequest = legacyPullRequest with { Title = "Fresh title", Body = "body that must not persist" };
        var source = new PullRequestRefreshSource(
            storage,
            (_, _) => Task.FromResult(new PullRequestFeedResult([freshPullRequest], [freshPullRequest], RepositoryMissing: false)),
            pollInterval: TimeSpan.FromMinutes(10));

        Assert.Equal(legacyPullRequest.Title, source.Current.Result.PullRequests[0].Title);
        await _WaitUntilAsync(() => storage.Raw("refreshSourceSnapshot").Contains(freshPullRequest.Title), TimeSpan.FromSeconds(2));
        source.Dispose();

        var persistedJson = storage.Raw("refreshSourceSnapshot");
        Assert.DoesNotContain(legacyPullRequest.Body, persistedJson);
        Assert.DoesNotContain(freshPullRequest.Body, persistedJson);

        var release = new TaskCompletionSource<PullRequestFeedResult>();
        var restarted = new PullRequestRefreshSource(storage, (_, _) => release.Task, pollInterval: TimeSpan.FromMinutes(10));

        Assert.Equal(freshPullRequest.Title, restarted.Current.Result.PullRequests[0].Title);
        Assert.Equal(freshPullRequest.Title, restarted.Current.Result.ReviewRequested[0].Title);
        Assert.Null(restarted.Current.Result.PullRequests[0].Body);
        Assert.False(restarted.Current.Result.RepositoryMissing);

        release.SetResult(PullRequestFeedResult.Missing);
        restarted.Dispose();
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
            new InMemoryPluginStorage(),
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

    // The JSON-backed test storage reproduces the host's deserialize path: malformed persisted data must fall
    // back to an empty snapshot instead of aborting plugin initialization.
    [Fact]
    public void ColdStart_WithUnparsableStoredJson_FallsBackToEmpty_InsteadOfThrowing()
    {
        var storage = new InMemoryPluginStorage();
        storage.SeedRaw("refreshSourceSnapshot", "not json at all");

        var source = new PullRequestRefreshSource(storage, (_, _) => Task.FromResult(new PullRequestFeedResult([], [], RepositoryMissing: false)), pollInterval: TimeSpan.FromMinutes(10));

        var current = source.Current;
        source.Dispose();

        Assert.Empty(current.Result.PullRequests);
        Assert.False(current.Result.RepositoryMissing);
        Assert.Null(current.FetchedAt);
    }

    // AC-515 blocker 2's other failure shape: valid JSON that simply is not this record's shape (e.g. a value
    // written under this key by something unrelated). `System.Text.Json.JsonSerializer` does not
    // throw for this — `PullRequestFeedSnapshot.Result` is a required, non-nullable reference, but a
    // missing JSON property still deserializes to a snapshot whose `Result` is null, since deserialization
    // does not enforce non-null reference members. A bare `?? Empty` on the constructor's read would miss
    // this: the deserialized object is not null, only its `Result` is — so `PullRequestRefreshSource`
    // must reject it explicitly rather than merely catch an exception that never comes.
    [Fact]
    public void ColdStart_WithWrongShapedJson_FallsBackToEmpty_InsteadOfCarryingANullResult()
    {
        var storage = new InMemoryPluginStorage();
        storage.SeedRaw("refreshSourceSnapshot", """{"totally":"unrelated","shape":true}""");

        var source = new PullRequestRefreshSource(storage, (_, _) => Task.FromResult(new PullRequestFeedResult([], [], RepositoryMissing: false)), pollInterval: TimeSpan.FromMinutes(10));

        var current = source.Current;
        source.Dispose();

        Assert.NotNull(current.Result);
        Assert.Empty(current.Result.PullRequests);
        Assert.Null(current.FetchedAt);
    }

    // A confirming review's follow-up on the same class of bug, one level deeper: a stored `{"Result":{}}`
    // deserializes to a non-null `PullRequestFeedResult` whose `PullRequests`/`ReviewRequested`
    // are themselves null — `System.Text.Json.JsonSerializer` enforces non-null reference members on
    // neither the record nor its positional parameters. A bare `Result: not null` check (the fix for the
    // blocker above) lets this one through; `GitHubPullRequestsWidget._ApplySnapshot`'s
    // `result.ReviewRequested.Select(...)` would throw a `NullReferenceException` rendering it.
    [Fact]
    public void ColdStart_WithNullCollectionsInsideResult_FallsBackToEmpty_InsteadOfCarryingNullLists()
    {
        var storage = new InMemoryPluginStorage();
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

    // Adversarial-review defect: `Dispose()` tore down the gate a still-running `PullRequestRefreshSource.RefreshAsync`
    // call was about to release into. This reproduces the exact shape — a call holding the gate and mid-`_load`
    // when `Dispose()` runs on top of it, then the load completing afterwards — by draining the constructor's
    // own due-time-zero tick first (same technique as `OverlappingRefreshCalls_CollapseIntoOneLoad`) so
    // the call under test is the only one holding the gate, then issuing it directly to get a real `Task{TResult}`
    // handle a fire-and-forget timer callback never gives the production code. Before the fix this call's task
    // faulted with `ObjectDisposedException` once `release` completed — unobserved in production,
    // since every real caller is `_ = RefreshAsync(...)`.
    [Fact]
    public async Task Dispose_WhileARefreshIsInFlight_DoesNotThrowFromTheGateItDisposes()
    {
        var initialTickSeen = new TaskCompletionSource();
        var emptyResult = new PullRequestFeedResult([], [], RepositoryMissing: false);
        var release = new TaskCompletionSource<PullRequestFeedResult>();
        var calls = 0;

        var source = new PullRequestRefreshSource(
            new InMemoryPluginStorage(),
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

    // The other half of the same defect: a call that had not even reached the gate yet when `Dispose()` ran —
    // `SemaphoreSlim.WaitAsync(int)` itself throws `ObjectDisposedException` unconditionally
    // once the semaphore is disposed, regardless of its count. Before the fix this propagated straight out of
    // `PullRequestRefreshSource.RefreshAsync`.
    [Fact]
    public async Task RefreshAsync_CalledAfterDispose_IsGatedOutInsteadOfThrowing()
    {
        var source = new PullRequestRefreshSource(
            new InMemoryPluginStorage(),
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
        var storage = new InMemoryPluginStorage();

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
            new InMemoryPluginStorage(),
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

}
