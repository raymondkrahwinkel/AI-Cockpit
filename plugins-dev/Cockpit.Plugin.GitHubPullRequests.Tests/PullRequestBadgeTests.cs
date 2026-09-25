extern alias UiAsm;

using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using NSubstitute;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Notifications;
using UiAsm::Cockpit.Plugin.GitHubPullRequests.UI;
using UiSettings = UiAsm::Cockpit.Plugin.GitHubPullRequests.Contracts.GitHubPullRequestsSettings;

namespace Cockpit.Plugin.GitHubPullRequests.Tests;

// AC-1396 acceptance 3: the badge is the UI part's, the count the backend's, and the two meet only on the channel.
// Wired through InProcessChannel with the real backend PullRequestBadgeUpdater on one side and the real UI
// PullRequestBadge on the other.
[Collection("avalonia")]
public class PullRequestBadgeTests
{
    private static readonly Contracts.GitHubPullRequest Mine = new(1, "Mine", "https://github.com/o/r/pull/1", null, "o/r", "me");
    private static readonly Contracts.GitHubPullRequest Asked = new(2, "Asked", "https://github.com/o/r/pull/2", null, "o/r", "you");
    private static readonly Contracts.GitHubPullRequest AlsoAsked = new(3, "Also asked", "https://github.com/o/r/pull/3", null, "o/r", "you");

    private static readonly Contracts.PullRequestFeedResult ThreeOpenTwoWaiting =
        new([Mine, Asked, AlsoAsked], [Asked, AlsoAsked], RepositoryMissing: false);

    [Fact]
    public void TheBadge_FollowsTheBackendsCount_OverTheChannel()
    {
        var channel = new InProcessChannel();
        var shown = new SideMenuButtonBadge();
        var host = TestUiHost.Create(channel);
        host.AddSideMenuButtonWithBadge("Open PRs", Arg.Any<Action>()).Returns(shown);
        using var badge = new PullRequestBadge(host, new UiSettings(host.Storage));

        using var updater = PullRequestBadgeUpdaterTests.Updater(
            channel,
            new Contracts.GitHubPullRequestsSettings(new InMemoryPluginStorage()),
            PullRequestBadgeUpdaterTests.PersistedSource(ThreeOpenTwoWaiting));

        Assert.Equal(3, shown.Primary);
        Assert.Equal(2, shown.Secondary);
    }

    [Fact]
    public void WithNoSubscriber_TheBadgeStaysUnknown_AndTheBackendKeepsCounting()
    {
        var channel = new InProcessChannel();
        var shown = new SideMenuButtonBadge();
        var host = TestUiHost.Create(channel);
        host.AddSideMenuButtonWithBadge("Open PRs", Arg.Any<Action>()).Returns(shown);
        new PullRequestBadge(host, new UiSettings(host.Storage)).Dispose();

        using var updater = PullRequestBadgeUpdaterTests.Updater(
            channel,
            new Contracts.GitHubPullRequestsSettings(new InMemoryPluginStorage()),
            PullRequestBadgeUpdaterTests.PersistedSource(ThreeOpenTwoWaiting));

        Assert.Null(shown.Secondary);
        Assert.Equal(2, updater.Counts.ReviewRequested);
    }

    // Found in review: the backend's first poll lands before any UI part exists (UI parts initialise only after every
    // backend part has), so its arrivals were published to nobody and already marked seen — never toasted.
    [Fact]
    public void AnArrivalCountedBeforeTheUiSubscribed_IsStillAnnounced_OnceItAsks() => HeadlessAvalonia.Run(() =>
    {
        var channel = new InProcessChannel();
        var settings = new Contracts.GitHubPullRequestsSettings(new InMemoryPluginStorage())
        {
            UseGitHubCli = true,
            SeenReviewRequests = new HashSet<string>(StringComparer.Ordinal),
        };
        using var updater = PullRequestBadgeUpdaterTests.Updater(channel, settings, PullRequestBadgeUpdaterTests.PersistedSource(ThreeOpenTwoWaiting));
        using var answers = channel.Handle(Contracts.GitHubPullRequestsChannel.BadgeCounts, (_, _) =>
            Task.FromResult(JsonSerializer.SerializeToElement(updater.Claim(), Contracts.GitHubPullRequestsChannel.Json)));

        var host = TestUiHost.Create(channel);
        using var badge = new PullRequestBadge(host, new UiSettings(host.Storage));
        Dispatcher.UIThread.RunJobs();

        host.Received(2).ShowToast(Arg.Is<string>(message => message.StartsWith("Review requested")), Arg.Any<PluginToastSeverity>(), Arg.Any<string?>(), Arg.Any<Action?>());
    });

    [Fact]
    public void ClickingTheBadge_OpensTheDialog_WithTheSharedSingleInstanceKey()
    {
        Action? clicked = null;
        var host = TestUiHost.Create();
        host.AddSideMenuButtonWithBadge("Open PRs", Arg.Do<Action>(onInvoke => clicked = onInvoke)).Returns(new SideMenuButtonBadge());
        using var badge = new PullRequestBadge(host, new UiSettings(host.Storage));

        var click = clicked ?? throw new InvalidOperationException("No badge was registered.");
        click();

        // The widget's "View all" opens under this same key — a second click here has to refocus that one window,
        // not stack a second one, which only holds if the key actually matches.
        host.Received(1).ShowDialogAsync("GitHub Pull Requests", Arg.Any<Func<Control>>(), "pull-requests", 1040, 700);
    }
}
