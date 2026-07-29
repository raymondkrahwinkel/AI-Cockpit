using Cockpit.App.ViewModels;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// Single-pane layout (#24 / Zoom) must show exactly one session — the selected one — while grid mode
/// shows them all. This is the regression guard for the "two sessions stacked top/bottom in single-pane"
/// bug: pane visibility is driven deterministically from <see cref="CockpitViewModel"/> on every
/// selection/layout change, not a per-item XAML binding that only worked in the previewer.
/// </summary>
public class SessionPaneVisibilityTests
{
    [Fact]
    public void SinglePane_ShowsOnlyTheSelectedSession()
    {
        var vm = new CockpitViewModel();
        vm.SelectedSession = vm.Sessions[0];

        vm.GlobalSingleSessionLayout = true;

        Assert.Same(vm.Sessions[0], Assert.Single(vm.Sessions, session => session.IsPaneVisible));
    }

    [Fact]
    public void Grid_ShowsEverySession()
    {
        var vm = new CockpitViewModel();
        vm.SelectedSession = vm.Sessions[0];
        vm.GlobalSingleSessionLayout = true;

        vm.GlobalSingleSessionLayout = false;

        Assert.All(vm.Sessions, session => Assert.True(session.IsPaneVisible));
    }

    [Fact]
    public void SwitchingSelection_InSinglePane_MovesVisibilityToTheNewSelection()
    {
        var vm = new CockpitViewModel();
        vm.SelectedSession = vm.Sessions[0];
        vm.GlobalSingleSessionLayout = true;

        vm.SelectedSession = vm.Sessions[1];

        Assert.Same(vm.Sessions[1], Assert.Single(vm.Sessions, session => session.IsPaneVisible));
    }
}
