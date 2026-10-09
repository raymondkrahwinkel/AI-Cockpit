using Cockpit.Plugin.GitHubPullRequests.Contracts;

namespace Cockpit.Plugin.GitHubPullRequests.Tests;

// AC-515: refreshing has to run independent of any view, never make a caller wait on a miss, survive a restart
// with the previous list marked old, and not cost more `gh` calls per unit time than before. Every test here
// drives `PullRequestRefreshSource` directly — no `GitHubPullRequestsWidget` or the
// AC-517 side-menu badge involved — via a fake load function (no `gh`, no network), which is exactly what
// acceptance criterion 2 asks for ("demonstrable with a test on the refresh source, not on a control").
public class PullRequestRefreshSourceTests
{
    private static readonly GitHubPullRequest SamplePullRequest = new(1, "Fix the thing", "https://github.com/octocat/hello-world/pull/1", null, "octocat/hello-world", "octocat");

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

        // AC-1516: no startup tick — its instant fake load could replace the restored snapshot before the read below.
        var freshPullRequest = legacyPullRequest with { Title = "Fresh title", Body = "body that must not persist" };
        var source = new PullRequestRefreshSource(
            storage,
            (_, _) => Task.FromResult(new PullRequestFeedResult([freshPullRequest], [freshPullRequest], RepositoryMissing: false)),
            pollInterval: TimeSpan.FromMinutes(10),
            firstTickDue: Timeout.InfiniteTimeSpan);

        Assert.Equal(legacyPullRequest.Title, source.Current.Result.PullRequests[0].Title);
        Assert.True(await source.RefreshAsync(forceRefresh: false));
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

    // AC-1416: Updated is raised while the refresh gate is held, so a newer refresh cannot publish ahead of an
    // older one that is still inside its handlers.

    // The JSON-backed test storage reproduces the host's deserialize path: malformed persisted data must fall
    // back to an empty snapshot instead of aborting plugin initialization.
    [Fact]
    public void ColdStart_WithUnparsableStoredJson_FallsBackToEmpty_InsteadOfThrowing()
    {
        var storage = new InMemoryPluginStorage();
        storage.SeedRaw("refreshSourceSnapshot", "not json at all");

        var source = new PullRequestRefreshSource(storage, _NeverLoads, pollInterval: TimeSpan.FromMinutes(10));

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

    // A confirming review's follow-up on the same class of bug, one level deeper: a stored `{"Result":{}}`
    // deserializes to a non-null `PullRequestFeedResult` whose `PullRequests`/`ReviewRequested`
    // are themselves null — `System.Text.Json.JsonSerializer` enforces non-null reference members on
    // neither the record nor its positional parameters. A bare `Result: not null` check (the fix for the
    // blocker above) lets this one through; `GitHubPullRequestsWidget._ApplySnapshot`'s
    // `result.ReviewRequested.Select(...)` would throw a `NullReferenceException` rendering it.

    // Adversarial-review defect: `Dispose()` tore down the gate a still-running `PullRequestRefreshSource.RefreshAsync`
    // call was about to release into. This reproduces the exact shape — a call holding the gate and mid-`_load`
    // when `Dispose()` runs on top of it, then the load completing afterwards — by suppressing the constructor's
    // startup tick (same technique as `OverlappingRefreshCalls_CollapseIntoOneLoad`) so
    // the call under test is the only one holding the gate, then issuing it directly to get a real `Task{TResult}`
    // handle a fire-and-forget timer callback never gives the production code. Before the fix this call's task
    // faulted with `ObjectDisposedException` once `release` completed — unobserved in production,
    // since every real caller is `_ = RefreshAsync(...)`.

    // The other half of the same defect: a call that had not even reached the gate yet when `Dispose()` ran —
    // `SemaphoreSlim.WaitAsync(int)` itself throws `ObjectDisposedException` unconditionally
    // once the semaphore is disposed, regardless of its count. Before the fix this propagated straight out of
    // `PullRequestRefreshSource.RefreshAsync`.

    // A load that never returns, for the cold-start tests: they assert that nothing has fetched yet, and the
    // constructor's due-time-zero tick would otherwise land between it and the read and stamp a FetchedAt on
    // it (AC-1122). Never completing is what makes "nothing has fetched yet" hold rather than usually hold.
    private static Task<PullRequestFeedResult> _NeverLoads(bool forceRefresh, CancellationToken cancellationToken) =>
        new TaskCompletionSource<PullRequestFeedResult>().Task;
}
