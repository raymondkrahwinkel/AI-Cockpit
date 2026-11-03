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
}
