using System.Text.Json;
using NSubstitute;
using Cockpit.Plugin.GitHubPullRequests.Contracts;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.GitHubPullRequests.Tests;

// AC-517: the badge updater replaces the old always-visible section, so it has to prove what the section used to
// give away for free by always being on screen — the null/zero distinction on the badge and the arrival toast
// surviving the section's removal. AC-1396: the counting half only, so what it proves is read from its Counts and
// from the BadgeChanged events it publishes; PullRequestBadgeTests covers the badge those events drive.
public class PullRequestBadgeUpdaterTests
{
    private static readonly GitHubPullRequest Mine = new(1, "Faster startup", "https://github.com/o/r/pull/1", null, "o/r", "me");

    [Fact]
    public void BeforeAnyFetch_TheCountsAreNotYetKnown_NotAGuessedZero()
    {
        var source = new PullRequestRefreshSource(new InMemoryPluginStorage(), (_, _) => Task.FromResult(PullRequestFeedResult.Missing), TimeSpan.FromMinutes(10));

        using var updater = Updater(new InProcessChannel(), new GitHubPullRequestsSettings(new InMemoryPluginStorage()), source);

        Assert.Null(updater.Counts.Mine);
        Assert.Null(updater.Counts.ReviewRequested);
    }

    [Fact]
    public void RepositoryMissing_TheCountsStayNotYetKnown_EvenAfterAFetchCompletes()
    {
        using var source = _SourceAfterItsFirstPoll((_, _) => Task.FromResult(PullRequestFeedResult.Missing));

        using var updater = Updater(new InProcessChannel(), new GitHubPullRequestsSettings(new InMemoryPluginStorage()), source);

        Assert.Null(updater.Counts.Mine);
        Assert.Null(updater.Counts.ReviewRequested);
    }

    [Fact]
    public void AfterAFetch_TheCountsAreReal_IncludingAGenuineZeroSecondary()
    {
        var result = new PullRequestFeedResult([Mine], [], RepositoryMissing: false);
        using var source = _SourceAfterItsFirstPoll((_, _) => Task.FromResult(result));

        using var updater = Updater(new InProcessChannel(), new GitHubPullRequestsSettings(new InMemoryPluginStorage()), source);

        Assert.Equal(1, updater.Counts.Mine);
        Assert.Equal(0, updater.Counts.ReviewRequested);
    }

    [Fact]
    public void AReviewRequestAlreadyWaitingOnFirstLoad_IsNotAnnounced()
    {
        var channel = new InProcessChannel();
        var result = new PullRequestFeedResult([Mine], [Mine], RepositoryMissing: false);
        using var source = _SourceAfterItsFirstPoll((_, _) => Task.FromResult(result));
        var settings = new GitHubPullRequestsSettings(new InMemoryPluginStorage()) { UseGitHubCli = true };

        using var updater = Updater(channel, settings, source);

        Assert.Empty(_Arrivals(channel));
    }

    [Fact]
    public async Task AReviewRequestThatArrivesAfterTheFirstLoad_IsAnnouncedOnce()
    {
        var channel = new InProcessChannel();
        var noRequests = new PullRequestFeedResult([Mine], [], RepositoryMissing: false);
        var withRequest = new PullRequestFeedResult([Mine], [Mine], RepositoryMissing: false);
        var next = noRequests;
        using var source = _SourceAfterItsFirstPoll((_, _) => Task.FromResult(next));
        var settings = new GitHubPullRequestsSettings(new InMemoryPluginStorage()) { UseGitHubCli = true };

        // The first load is the one the source polled for itself above, so the updater primes its seen-set off
        // that snapshot the moment it is built — no request has "arrived" yet.
        using var updater = Updater(channel, settings, source);
        Assert.Empty(_Arrivals(channel));

        next = withRequest;
        await source.RefreshAsync(forceRefresh: true);

        Assert.Equal(Mine.Url, Assert.Single(_Arrivals(channel)).Url);

        // A second refresh that still carries the same request must not announce it again.
        await source.RefreshAsync(forceRefresh: true);
        Assert.Single(_Arrivals(channel));
    }

    [Fact]
    public void StartingWithAPersistedSnapshot_CountsItImmediately_AndDoesNotRepeatAnAlreadySeenRequest()
    {
        var channel = new InProcessChannel();
        var settings = new GitHubPullRequestsSettings(new InMemoryPluginStorage())
        {
            UseGitHubCli = true,
            SeenReviewRequests = new HashSet<string>(StringComparer.Ordinal) { ReviewRequestInbox.KeyOf(Mine) },
        };

        using var updater = Updater(channel, settings, PersistedSource(new PullRequestFeedResult([Mine], [Mine], RepositoryMissing: false)));

        Assert.Equal(1, updater.Counts.Mine);
        Assert.Equal(1, updater.Counts.ReviewRequested);

        // Mine's review request was already in SeenReviewRequests before this instance ever started — a restart
        // must not re-announce a request the operator already knew about.
        Assert.Empty(_Arrivals(channel));
    }

    // A source that starts from a snapshot persisted by an earlier run and never finishes a fetch of its own, so
    // that snapshot is the only thing there is to count.
    internal static PullRequestRefreshSource PersistedSource(PullRequestFeedResult result)
    {
        var storage = new InMemoryPluginStorage();
        storage.Set("refreshSourceSnapshot", new PullRequestFeedSnapshot(result, DateTimeOffset.UtcNow));
        return new PullRequestRefreshSource(storage, (_, _) => new TaskCompletionSource<PullRequestFeedResult>().Task, TimeSpan.FromMinutes(10));
    }

    internal static PullRequestBadgeUpdater Updater(InProcessChannel channel, GitHubPullRequestsSettings settings, PullRequestRefreshSource source) =>
        new(channel, Substitute.For<ICockpitSessionObserver>(), settings, source);

    private static List<GitHubPullRequest> _Arrivals(InProcessChannel channel) =>
        [.. channel.Published
            .Where(published => published.Name == GitHubPullRequestsChannel.BadgeChanged)
            .SelectMany(published => published.Payload.Deserialize<PullRequestBadgeState>(GitHubPullRequestsChannel.Json)?.Arrived ?? [])];

    // A source whose own startup poll has already landed (AC-1250, AC-1122). `PullRequestRefreshSource` fetches the
    // moment it exists — due time zero — so a test that then forces its own refresh is racing that poll through a
    // gate that silently drops whichever call loses, leaving the order its assertions depend on up to scheduling.
    private static PullRequestRefreshSource _SourceAfterItsFirstPoll(Func<bool, CancellationToken, Task<PullRequestFeedResult>> load)
    {
        using var polled = new ManualResetEventSlim();
        var source = new PullRequestRefreshSource(new InMemoryPluginStorage(), load, TimeSpan.FromMinutes(10));
        source.Updated += OnUpdated;

        // Subscribing first is what makes the `Current` check below safe rather than a second race: a poll landing
        // between the two is seen twice, never missed.
        if (source.Current.FetchedAt is null)
        {
            Assert.True(polled.Wait(TimeSpan.FromSeconds(30)), "The refresh source's startup poll never landed.");
        }

        source.Updated -= OnUpdated;
        return source;

        void OnUpdated(object? sender, PullRequestFeedSnapshot snapshot) => polled.Set();
    }
}
