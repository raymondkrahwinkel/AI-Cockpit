using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Workspaces;
using Cockpit.Core.Workspaces;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Core.Tests.Workspaces;

/// <summary>
/// Reordering the tab strip by dragging (Raymond, 2026-07-15: "we moeten de volgorde van de workspaces kunnen
/// aanpassen"). The order persists, and reordering never moves the selection — rearranging the desks is not
/// the same as walking to a different one.
/// </summary>
public class WorkspaceReorderTests
{
    [Fact]
    public void WithMoved_ForwardsTheFirstWorkspace_ClosesTheGapBehindIt()
    {
        var settings = _Three(out var a, out var b, out var c);

        var moved = settings.WithMoved(a.Id, 2);

        moved.Workspaces.Select(workspace => workspace.Id).Should().Equal(b.Id, c.Id, a.Id);
    }

    [Fact]
    public void WithMoved_BackwardsTheLastWorkspace()
    {
        var settings = _Three(out var a, out var b, out var c);

        var moved = settings.WithMoved(c.Id, 0);

        moved.Workspaces.Select(workspace => workspace.Id).Should().Equal(c.Id, a.Id, b.Id);
    }

    [Fact]
    public void WithMoved_PastTheEnd_LandsOnTheEnd_RatherThanBeingRefused()
    {
        var settings = _Three(out var a, out var b, out var c);

        settings.WithMoved(a.Id, 99).Workspaces.Select(workspace => workspace.Id).Should().Equal(b.Id, c.Id, a.Id);
        settings.WithMoved(c.Id, -5).Workspaces.Select(workspace => workspace.Id).Should().Equal(c.Id, a.Id, b.Id);
    }

    [Fact]
    public void WithMoved_LeavesTheSelectionAlone()
    {
        var settings = _Three(out var a, out var b, out _);
        settings = settings.WithActive(b.Id);

        settings.WithMoved(a.Id, 2).ActiveWorkspaceId.Should().Be(b.Id);
    }

    [Fact]
    public void WithMoved_ToItsOwnIndex_IsANoOp()
    {
        var settings = _Three(out var a, out _, out _);

        settings.WithMoved(a.Id, 0).Should().BeSameAs(settings);
    }

    [Fact]
    public void WithMoved_AnUnknownWorkspace_IsIgnored()
    {
        var settings = _Three(out _, out _, out _);

        settings.WithMoved("gone", 0).Should().BeSameAs(settings);
    }

    [Fact]
    public async Task MoveWorkspaceAsync_ReordersTheTabsAndPersists_SoTheOrderSurvivesARestart()
    {
        var store = Substitute.For<IWorkspaceSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(WorkspaceSettings.Default);
        var viewModel = new WorkspacesViewModel(store);
        await viewModel.AddWorkspaceCommand.ExecuteAsync(WorkspaceType.Dashboard);
        var first = viewModel.Settings.Workspaces[0];
        store.ClearReceivedCalls();

        await viewModel.MoveWorkspaceAsync(first.Id, 1);

        viewModel.Tabs.Select(tab => tab.Id).Should().Equal(viewModel.Settings.Workspaces.Select(workspace => workspace.Id));
        viewModel.Settings.Workspaces[1].Id.Should().Be(first.Id);
        await store.Received(1).SaveAsync(Arg.Any<WorkspaceSettings>(), Arg.Any<CancellationToken>());
    }

    private static WorkspaceSettings _Three(out Workspace a, out Workspace b, out Workspace c)
    {
        a = Workspace.Create("A", WorkspaceType.Sessions);
        b = Workspace.Create("B", WorkspaceType.Sessions);
        c = Workspace.Create("C", WorkspaceType.Dashboard);
        return new WorkspaceSettings { Workspaces = [a, b, c], ActiveWorkspaceId = a.Id };
    }
}
