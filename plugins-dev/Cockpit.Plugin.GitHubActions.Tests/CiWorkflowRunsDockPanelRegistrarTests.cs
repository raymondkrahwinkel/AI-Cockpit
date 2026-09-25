extern alias UiAsm;

using NSubstitute;
using Cockpit.Plugins.Abstractions.Docking;
using Cockpit.Plugins.Abstractions.UI;
using CiWorkflowRunsDockPanelRegistrar = UiAsm::Cockpit.Plugin.GitHubActions.UI.CiWorkflowRunsDockPanelRegistrar;

namespace Cockpit.Plugin.GitHubActions.Tests;

// AC-1065: the dock-rail registration, mirroring PullRequestDockPanelRegistrarTests' own coverage of the same
// registration shape. AC-1394: ICockpitUiHost is an unconditional contract (no host predates it), so the
// old-host reflection guard this used to cover no longer applies and has no test of its own any more.
public class CiWorkflowRunsDockPanelRegistrarTests
{
    [Fact]
    public void Register_AddsThePanel_WithTheStableIdAndTitle()
    {
        var host = Substitute.For<ICockpitUiHost>();

        CiWorkflowRunsDockPanelRegistrar.Register(host);

        host.Received(1).AddDockPanel(Arg.Is<DockPanelRegistration>(panel =>
            panel.Id == "github.actions" && panel.Title == "GitHub Actions"));
    }
}
