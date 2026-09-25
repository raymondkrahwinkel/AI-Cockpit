extern alias UiAsm;

using NSubstitute;
using Cockpit.Plugins.Abstractions.Docking;
using UiAsm::Cockpit.Plugin.GitHubPullRequests.Contracts;
using UiAsm::Cockpit.Plugin.GitHubPullRequests.UI;

namespace Cockpit.Plugin.GitHubPullRequests.Tests;

// AC-960: the dock-rail registration. AC-1396: through the UI host, which always has AddDockPanel, so the
// older-host guard and its two tests went with the split.
public class PullRequestDockPanelRegistrarTests
{
    [Fact]
    public void Register_AddsThePanel_WithTheStableIdAndTitle()
    {
        var host = TestUiHost.Create();

        PullRequestDockPanelRegistrar.Register(host, new GitHubPullRequestsSettings(new InMemoryPluginStorage()));

        host.Received(1).AddDockPanel(Arg.Is<DockPanelRegistration>(panel => panel.Id == "github.pull-requests" && panel.Title == "Pull Requests"));
    }
}
