using Avalonia.Threading;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.GitHubPullRequests.Tests;

// AC-517: the badge updater replaces the old always-visible section, so it has to prove three things the section
// used to give away for free by always being on screen — the null/zero distinction on the badge, the toast
// surviving the section's removal, and an older host's missing `AddSideMenuButtonWithBadge` not taking the
// rest of the plugin down with it.
[Collection("avalonia")]
public class PullRequestBadgeUpdaterTests
{
    private static readonly GitHubPullRequest Mine = new(1, "Faster startup", "https://github.com/o/r/pull/1", null, "o/r", "me");

    [Fact]
    public void BeforeAnyFetch_TheBadgeIsNotYetKnown_NotAGuessedZero() => HeadlessAvalonia.Run(() =>
    {
        var host = new TestBadgeHost();
        var source = new PullRequestRefreshSource(new InMemoryPluginStorage(), (_, _) => Task.FromResult(PullRequestFeedResult.Missing), TimeSpan.FromMinutes(10));
        var settings = new GitHubPullRequestsSettings(new InMemoryPluginStorage());

        using var updater = new PullRequestBadgeUpdater(host, settings, source);

        var badge = _RegisteredBadge(host);
        Assert.Null(badge.Primary);
        Assert.Null(badge.Secondary);
    });

    [Fact]
    public void RepositoryMissing_TheBadgeStaysNotYetKnown_EvenAfterAFetchCompletes() => HeadlessAvalonia.Run(() =>
        _RunAsync(async () =>
        {
            var host = new TestBadgeHost();
            var source = new PullRequestRefreshSource(new InMemoryPluginStorage(), (_, _) => Task.FromResult(PullRequestFeedResult.Missing), TimeSpan.FromMinutes(10));
            var settings = new GitHubPullRequestsSettings(new InMemoryPluginStorage());

            using var updater = new PullRequestBadgeUpdater(host, settings, source);
            await source.RefreshAsync(forceRefresh: true);

            var badge = _RegisteredBadge(host);
            Assert.Null(badge.Primary);
            Assert.Null(badge.Secondary);
        }));

    [Fact]
    public void AfterAFetch_TheBadgeShowsRealCounts_IncludingAGenuineZeroSecondary() => HeadlessAvalonia.Run(() =>
        _RunAsync(async () =>
        {
            var host = new TestBadgeHost();
            var result = new PullRequestFeedResult([Mine], [], RepositoryMissing: false);
            var source = new PullRequestRefreshSource(new InMemoryPluginStorage(), (_, _) => Task.FromResult(result), TimeSpan.FromMinutes(10));
            var settings = new GitHubPullRequestsSettings(new InMemoryPluginStorage());

            using var updater = new PullRequestBadgeUpdater(host, settings, source);
            await source.RefreshAsync(forceRefresh: true);

            var badge = _RegisteredBadge(host);
            Assert.Equal(1, badge.Primary);
            Assert.Equal(0, badge.Secondary);
        }));

    [Theory]
    [InlineData(typeof(MissingMethodException))]
    [InlineData(typeof(TypeLoadException))]
    public void AnOlderHostWithNoBadgeSupport_DoesNotTakeThePluginDown(Type exceptionType) => HeadlessAvalonia.Run(() =>
        _RunAsync(async () =>
        {
            var host = new TestBadgeHost { BadgeUnsupportedException = () => (Exception)Activator.CreateInstance(exceptionType)! };
            var result = new PullRequestFeedResult([Mine], [Mine], RepositoryMissing: false);
            var source = new PullRequestRefreshSource(new InMemoryPluginStorage(), (_, _) => Task.FromResult(result), TimeSpan.FromMinutes(10));
            var settings = new GitHubPullRequestsSettings(new InMemoryPluginStorage()) { UseGitHubCli = true };

            // Neither construction nor a snapshot update may throw just because the host's Abstractions predates
            // AC-516 — that is exactly what the updater's own try/catch exists for, whether resolution fails on the
            // missing method (MissingMethodException) or on the missing SideMenuButtonBadge type itself
            // (TypeLoadException) — the latter is the realistic failure, since an old host lacks both.
            using var updater = new PullRequestBadgeUpdater(host, settings, source);
            await source.RefreshAsync(forceRefresh: true);
            Dispatcher.UIThread.RunJobs();

            Assert.Empty(host.RegisteredBadgeTitles);
        }));

    [Fact]
    public void AReviewRequestAlreadyWaitingOnFirstLoad_IsNotAnnounced() => HeadlessAvalonia.Run(() =>
        _RunAsync(async () =>
        {
            var host = new TestBadgeHost();
            var result = new PullRequestFeedResult([Mine], [Mine], RepositoryMissing: false);
            var source = new PullRequestRefreshSource(new InMemoryPluginStorage(), (_, _) => Task.FromResult(result), TimeSpan.FromMinutes(10));
            var settings = new GitHubPullRequestsSettings(new InMemoryPluginStorage()) { UseGitHubCli = true };

            using var updater = new PullRequestBadgeUpdater(host, settings, source);
            await source.RefreshAsync(forceRefresh: true);
            Dispatcher.UIThread.RunJobs();

            Assert.Empty(host.Toasts);
        }));

    [Fact]
    public void AReviewRequestThatArrivesAfterTheFirstLoad_RaisesOneToast() => HeadlessAvalonia.Run(() =>
        _RunAsync(async () =>
        {
            var host = new TestBadgeHost();
            var noRequests = new PullRequestFeedResult([Mine], [], RepositoryMissing: false);
            var withRequest = new PullRequestFeedResult([Mine], [Mine], RepositoryMissing: false);
            var next = noRequests;
            var source = new PullRequestRefreshSource(new InMemoryPluginStorage(), (_, _) => Task.FromResult(next), TimeSpan.FromMinutes(10));
            var settings = new GitHubPullRequestsSettings(new InMemoryPluginStorage()) { UseGitHubCli = true };

            using var updater = new PullRequestBadgeUpdater(host, settings, source);
            await source.RefreshAsync(forceRefresh: true);
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(host.Toasts);

            next = withRequest;
            await source.RefreshAsync(forceRefresh: true);
            Dispatcher.UIThread.RunJobs();

            var toast = Assert.Single(host.Toasts);
            Assert.Contains(Mine.Repository, toast, StringComparison.Ordinal);

            // A second refresh that still carries the same request must not repeat the toast.
            await source.RefreshAsync(forceRefresh: true);
            Dispatcher.UIThread.RunJobs();
            Assert.Single(host.Toasts);
        }));

    [Fact]
    public void ClickingTheBadge_OpensTheDialog_WithTheSharedSingleInstanceKey() => HeadlessAvalonia.Run(() =>
    {
        var host = new TestBadgeHost();
        var source = new PullRequestRefreshSource(new InMemoryPluginStorage(), (_, _) => Task.FromResult(PullRequestFeedResult.Missing), TimeSpan.FromMinutes(10));
        var settings = new GitHubPullRequestsSettings(new InMemoryPluginStorage());

        using var updater = new PullRequestBadgeUpdater(host, settings, source);

        Assert.NotNull(host.BadgeClicked);
        host.BadgeClicked!();

        var dialog = Assert.Single(host.DialogsShown);
        Assert.Equal("GitHub Pull Requests", dialog.Title);

        // The old section's and the widget's "View all" both open under this same key — a second click here has
        // to refocus that one window, not stack a second one, which only holds if the key actually matches theirs.
        Assert.Equal("pull-requests", dialog.SingleInstanceKey);
    });

    [Fact]
    public void StartingWithAPersistedSnapshot_ShowsItsCountsImmediately_AndDoesNotRepeatAnAlreadySeenRequest() => HeadlessAvalonia.Run(() =>
    {
        var host = new TestBadgeHost();
        var refreshStorage = new InMemoryPluginStorage();
        var persisted = new PullRequestFeedSnapshot(new PullRequestFeedResult([Mine], [Mine], RepositoryMissing: false), DateTimeOffset.UtcNow);
        refreshStorage.Set("refreshSourceSnapshot", persisted);

        var settings = new GitHubPullRequestsSettings(new InMemoryPluginStorage())
        {
            UseGitHubCli = true,
            SeenReviewRequests = new HashSet<string>(StringComparer.Ordinal) { ReviewRequestInbox.KeyOf(Mine) },
        };

        // A pollInterval long enough, and a load function that never returns, that no real fetch can land during
        // the test — the persisted snapshot above is the only thing the constructor has to go on.
        var source = new PullRequestRefreshSource(refreshStorage, (_, _) => new TaskCompletionSource<PullRequestFeedResult>().Task, TimeSpan.FromMinutes(10));

        using var updater = new PullRequestBadgeUpdater(host, settings, source);
        Dispatcher.UIThread.RunJobs();

        var badge = _RegisteredBadge(host);
        Assert.Equal(1, badge.Primary);
        Assert.Equal(1, badge.Secondary);

        // Mine's review request was already in SeenReviewRequests before this instance ever started — a restart
        // must not re-announce a request the operator already knew about.
        Assert.Empty(host.Toasts);
    });

    private static SideMenuButtonBadge _RegisteredBadge(TestBadgeHost host)
    {
        Assert.Equal("Open PRs", Assert.Single(host.RegisteredBadgeTitles));
        return host.LastBadge ?? throw new InvalidOperationException("No badge was registered.");
    }

    private static void _RunAsync(Func<Task> body) => body().GetAwaiter().GetResult();
}
