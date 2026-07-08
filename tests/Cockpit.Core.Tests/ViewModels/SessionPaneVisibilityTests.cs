using Cockpit.App.ViewModels;
using FluentAssertions;

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

        vm.SingleSessionLayout = true;

        vm.Sessions.Where(session => session.IsPaneVisible).Should().ContainSingle()
            .Which.Should().BeSameAs(vm.Sessions[0]);
    }

    [Fact]
    public void Grid_ShowsEverySession()
    {
        var vm = new CockpitViewModel();
        vm.SelectedSession = vm.Sessions[0];
        vm.SingleSessionLayout = true;

        vm.SingleSessionLayout = false;

        vm.Sessions.Should().OnlyContain(session => session.IsPaneVisible);
    }

    [Fact]
    public void SwitchingSelection_InSinglePane_MovesVisibilityToTheNewSelection()
    {
        var vm = new CockpitViewModel();
        vm.SelectedSession = vm.Sessions[0];
        vm.SingleSessionLayout = true;

        vm.SelectedSession = vm.Sessions[1];

        vm.Sessions.Where(session => session.IsPaneVisible).Should().ContainSingle()
            .Which.Should().BeSameAs(vm.Sessions[1]);
    }
}
