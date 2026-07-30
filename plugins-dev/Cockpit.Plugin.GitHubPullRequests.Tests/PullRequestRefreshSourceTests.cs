using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.GitHubPullRequests.Tests;

/// <summary>
/// AC-515: refreshing has to run independent of any view, never make a caller wait on a miss, survive a restart
/// with the previous list marked old, and not cost more `gh` calls per unit time than before. Every test here
/// drives <see cref="PullRequestRefreshSource"/> directly — no <see cref="GitHubPullRequestsSideSectionControl"/>
/// or <see cref="GitHubPullRequestsWidget"/> involved — via a fake load function (no `gh`, no network), which is
/// exactly what acceptance criterion 2 asks for ("aantoonbaar met een test op de verversingsbron, niet op een control").
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

        // Nothing here ever builds a SideSectionControl, a Widget, or attaches anything to a visual tree — the
        // background poll firing more than once is entirely the source's own doing.
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
        await _WaitUntilAsync(() => source.Current.Result.PullRequests.Count > 0 && source.Current.Result.PullRequests[0].Title == freshPullRequest.Title, TimeSpan.FromSeconds(2));

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

    [Fact]
    public void StaleAfter_IsThreeTimesTheGhClientTtl_NotARoundedNumber()
    {
        // Asserted against the constant itself, not a literal like TimeSpan.FromMinutes(15) — a change to the
        // client's own TTL must not silently desynchronise the marker's threshold from what the doc comment claims.
        Assert.Equal(GitHubPrGhClient.PullRequestTtl * 3, PullRequestRefreshSource.StaleAfter);
    }

    [Fact]
    public async Task AFailedFetch_StillRaisesUpdated_SoAFirstEverAttemptIsNotSilent()
    {
        var storage = new InMemoryStorage();
        var source = new PullRequestRefreshSource(
            storage,
            (_, _) => Task.FromException<PullRequestFeedResult>(new InvalidOperationException("gh not installed")),
            pollInterval: TimeSpan.FromMinutes(10));

        PullRequestFeedSnapshot? received = null;
        source.Updated += (_, snapshot) => received = snapshot;

        await _WaitUntilAsync(() => received is not null, TimeSpan.FromSeconds(2));

        source.Dispose();

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

    private sealed class InMemoryStorage : IPluginStorage
    {
        private readonly Dictionary<string, object?> _values = [];

        public T? Get<T>(string key) => _values.TryGetValue(key, out var value) ? (T?)value : default;

        public void Set<T>(string key, T value) => _values[key] = value;
    }
}
