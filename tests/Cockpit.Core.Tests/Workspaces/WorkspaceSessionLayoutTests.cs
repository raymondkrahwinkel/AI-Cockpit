using Cockpit.App.ViewModels;
using Cockpit.Core.Workspaces;
using FluentAssertions;

namespace Cockpit.Core.Tests.Workspaces;

/// <summary>
/// A Sessions workspace may arrange itself instead of following Options (Raymond, 2026-07-15: "by default
/// volgt die de algemene instellingen, maar overriden per session workspace"). Three values live here and it
/// matters which is which: the global default Options edits, a workspace's override, and the effective value
/// the grid draws from.
/// </summary>
public class WorkspaceSessionLayoutTests
{
    [Fact]
    public void WithoutAnOverride_TheWorkspaceFollowsOptions()
    {
        var vm = _CreateWithSessionsWorkspace();

        vm.GlobalSingleSessionLayout = true;
        vm.GlobalStackSessionsVertically = true;

        vm.SingleSessionLayout.Should().BeTrue();
        vm.StackSessionsVertically.Should().BeTrue();
        vm.WorkspaceFollowsGlobalLayout.Should().BeTrue();
    }

    [Fact]
    public void AnOverride_WinsOverOptions()
    {
        var vm = _CreateWithSessionsWorkspace();
        vm.GlobalSingleSessionLayout = true;

        vm.WorkspaceFollowsGlobalLayout = false;
        vm.WorkspaceSingleSessionLayout = false;

        vm.SingleSessionLayout.Should().BeFalse();
        vm.WorkspaceFollowsGlobalLayout.Should().BeFalse();
    }

    /// <summary>
    /// Taking control must not rearrange anything by itself: the override starts from what the desk is already
    /// doing. A checkbox that reshuffles your sessions the moment you tick it is one nobody ticks twice.
    /// </summary>
    [Fact]
    public void TakingControl_StartsFromWhatTheWorkspaceIsAlreadyDoing()
    {
        var vm = _CreateWithSessionsWorkspace();
        vm.GlobalSingleSessionLayout = true;
        vm.GlobalStackSessionsVertically = true;

        vm.WorkspaceFollowsGlobalLayout = false;

        vm.SingleSessionLayout.Should().BeTrue();
        vm.StackSessionsVertically.Should().BeTrue();
    }

    /// <summary>
    /// The trap this design exists to avoid: Options edits the default and nothing else. If it bound to the
    /// effective value, opening it on an overriding workspace would save that workspace's choice over the
    /// default for every other desk.
    /// </summary>
    [Fact]
    public void AnOverride_NeverWritesItselfIntoTheGlobalDefault()
    {
        var vm = _CreateWithSessionsWorkspace();
        vm.GlobalSingleSessionLayout = false;

        vm.WorkspaceFollowsGlobalLayout = false;
        vm.WorkspaceSingleSessionLayout = true;

        vm.GlobalSingleSessionLayout.Should().BeFalse("Options holds the default, which this workspace only overrode for itself");
        vm.SingleSessionLayout.Should().BeTrue();
    }

    [Fact]
    public void HandingItBack_FollowsOptionsAgain()
    {
        var vm = _CreateWithSessionsWorkspace();
        vm.GlobalSingleSessionLayout = false;
        vm.WorkspaceFollowsGlobalLayout = false;
        vm.WorkspaceSingleSessionLayout = true;

        vm.WorkspaceFollowsGlobalLayout = true;

        vm.SingleSessionLayout.Should().BeFalse();
        vm.Workspaces.Active!.SingleSessionLayout.Should().BeNull("null is what following Options looks like on disk");
    }

    /// <summary>An override belongs to its own desk: switching to one that never took control follows Options again.</summary>
    [Fact]
    public async Task SwitchingWorkspaces_ReReadsTheEffectiveLayout()
    {
        var vm = _CreateWithSessionsWorkspace();
        vm.GlobalSingleSessionLayout = false;
        vm.WorkspaceFollowsGlobalLayout = false;
        vm.WorkspaceSingleSessionLayout = true;
        var overriding = vm.Workspaces.Active!.Id;

        await vm.Workspaces.AddWorkspaceCommand.ExecuteAsync(WorkspaceType.Sessions);
        var plain = vm.Workspaces.Active!.Id;
        plain.Should().NotBe(overriding);

        vm.SingleSessionLayout.Should().BeFalse("this desk never took control, so it follows Options");
        vm.WorkspaceFollowsGlobalLayout.Should().BeTrue();
    }

    /// <summary>A dashboard has its own grid; these two are not its to set, and it must not be able to hold them.</summary>
    [Fact]
    public async Task ADashboard_IgnoresTheSessionLayoutOverrides()
    {
        var vm = _CreateWithSessionsWorkspace();
        await vm.Workspaces.AddWorkspaceCommand.ExecuteAsync(WorkspaceType.Dashboard);
        var dashboard = vm.Workspaces.Active!;

        await vm.Workspaces.SetSessionLayoutAsync(dashboard.Id, singleSession: true, stackVertically: true);

        vm.Workspaces.Active!.SingleSessionLayout.Should().BeNull();
        vm.Workspaces.Active!.StackSessionsVertically.Should().BeNull();
    }

    private static CockpitViewModel _CreateWithSessionsWorkspace()
    {
        var vm = new CockpitViewModel();
        vm.Workspaces.EnsureSessionWorkspace();

        return vm;
    }
}
