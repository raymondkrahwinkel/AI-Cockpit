extern alias UiAsm;

using NSubstitute;
using UiAsm::Cockpit.Plugin.GitHubPullRequests.Contracts;
using UiAsm::Cockpit.Plugin.GitHubPullRequests.UI;

namespace Cockpit.Plugin.GitHubPullRequests.Tests;

// AC-1396 acceptance 4 (D6): "add to prompt" lands in the session this window has selected, handed on by id — the
// backend part has no active session to guess from. Two sessions, the second one active.
public class PullRequestActionsTests
{
    [Fact]
    public async Task Inject_LandsInTheSessionTheWindowHasActive_NotAnotherOne()
    {
        var host = TestUiHost.Create();
        host.ActivePaneId.Returns("second-pane");
        var pullRequest = new GitHubPullRequest(7, "Tidy up", "https://github.com/o/r/pull/7", null, "o/r", "me");

        await PullRequestActions.InjectAsync(host, new GitHubPullRequestsSettings(host.Storage), pullRequest);

        await host.Received(1).SendToSessionAsync("second-pane", Arg.Is<string>(prompt => prompt.Contains("#7")));
        await host.DidNotReceive().SendToSessionAsync("first-pane", Arg.Any<string>());
        await host.DidNotReceive().SetClipboardTextAsync(Arg.Any<string>());
    }
}
