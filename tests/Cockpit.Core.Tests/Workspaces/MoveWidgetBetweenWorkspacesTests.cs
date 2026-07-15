using Cockpit.App.ViewModels;
using Cockpit.Core.Workspaces;
using FluentAssertions;

namespace Cockpit.Core.Tests.Workspaces;

/// <summary>
/// Dragging a widget onto another dashboard's tab moves it there (F5). The gesture is the view's; this is what
/// it asks for, and the part that can be held to account without a pointer.
/// </summary>
public class MoveWidgetBetweenWorkspacesTests
{
    [Fact]
    public async Task AWidget_MovesToTheDashboardItWasDroppedOn()
    {
        var (workspaces, source, target) = await _TwoDashboardsAsync();
        var pane = source.Panes.Single();

        var moved = await workspaces.MovePaneToWorkspaceAsync(pane.Id, target.Id);

        moved.Should().BeTrue();
        _Workspace(workspaces, source.Id).Panes.Should().BeEmpty();
        _Workspace(workspaces, target.Id).Panes.Should().ContainSingle().Which.WidgetId.Should().Be("widgets.clock");
    }

    /// <summary>
    /// The pane keeps its id, which is what carries the widget's settings — instance storage is keyed by it. A
    /// moved system monitor has to arrive still showing the metrics it was set to, not reset to defaults.
    /// </summary>
    [Fact]
    public async Task AMovedWidget_KeepsTheInstanceIdItsSettingsAreKeyedBy()
    {
        var (workspaces, source, target) = await _TwoDashboardsAsync();
        var paneId = source.Panes.Single().Id;

        await workspaces.MovePaneToWorkspaceAsync(paneId, target.Id);

        _Workspace(workspaces, target.Id).Panes.Single().Id.Should().Be(paneId);
    }

    /// <summary>
    /// It lands on the target's first free cell at its own size, rather than wherever the pointer happened to
    /// be: the other desk's arrangement is not the operator's to disturb from this one.
    /// </summary>
    [Fact]
    public async Task AMovedWidget_KeepsItsSize_AndTakesAFreeCellOnTheTarget()
    {
        var (workspaces, source, target) = await _TwoDashboardsAsync();
        await workspaces.SelectWorkspaceCommand.ExecuteAsync(target.Id);
        await workspaces.AddWidgetAsync("widgets.system-monitor", columnSpan: 6, rowSpan: 6);
        await workspaces.SelectWorkspaceCommand.ExecuteAsync(source.Id);
        var moving = _Workspace(workspaces, source.Id).Panes.Single();

        await workspaces.MovePaneToWorkspaceAsync(moving.Id, target.Id);

        var landed = _Workspace(workspaces, target.Id).Panes.Single(pane => pane.Id == moving.Id);
        landed.Cell.ColumnSpan.Should().Be(moving.Cell.ColumnSpan);
        landed.Cell.RowSpan.Should().Be(moving.Cell.RowSpan);
        _Workspace(workspaces, target.Id).Panes.Should().HaveCount(2);
    }

    /// <summary>A sessions workspace cannot hold a widget, so its tab is not a target — the move is refused, not attempted.</summary>
    [Fact]
    public async Task AWidget_DoesNotMoveToASessionsWorkspace()
    {
        var (workspaces, source, _) = await _TwoDashboardsAsync();
        var sessions = workspaces.EnsureSessionWorkspace();
        await workspaces.SelectWorkspaceCommand.ExecuteAsync(source.Id);
        var paneId = _Workspace(workspaces, source.Id).Panes.Single().Id;

        var moved = await workspaces.MovePaneToWorkspaceAsync(paneId, sessions);

        moved.Should().BeFalse();
        _Workspace(workspaces, source.Id).Panes.Should().ContainSingle();
    }

    /// <summary>Dropped back on its own tab, nothing happens — a move to where it already is is not a move.</summary>
    [Fact]
    public async Task AWidget_DroppedOnItsOwnWorkspace_DoesNothing()
    {
        var (workspaces, source, _) = await _TwoDashboardsAsync();
        var paneId = source.Panes.Single().Id;

        var moved = await workspaces.MovePaneToWorkspaceAsync(paneId, source.Id);

        moved.Should().BeFalse();
        _Workspace(workspaces, source.Id).Panes.Should().ContainSingle();
    }

    [Fact]
    public async Task AnUnknownPane_IsRefusedRatherThanThrowing()
    {
        var (workspaces, _, target) = await _TwoDashboardsAsync();

        var moving = async () => await workspaces.MovePaneToWorkspaceAsync("no-such-pane", target.Id);

        await moving.Should().NotThrowAsync();
    }

    private static Workspace _Workspace(WorkspacesViewModel workspaces, string id) =>
        workspaces.Settings.Workspaces.Single(workspace => workspace.Id == id);

    /// <summary>A dashboard holding one clock, and an empty second dashboard, with the first one showing.</summary>
    private static async Task<(WorkspacesViewModel Workspaces, Workspace Source, Workspace Target)> _TwoDashboardsAsync()
    {
        var workspaces = new WorkspacesViewModel();
        await workspaces.AddWorkspaceCommand.ExecuteAsync(WorkspaceType.Dashboard);
        var source = workspaces.Active!;
        await workspaces.AddWidgetAsync("widgets.clock", columnSpan: 6, rowSpan: 4);

        await workspaces.AddWorkspaceCommand.ExecuteAsync(WorkspaceType.Dashboard);
        var target = workspaces.Active!;
        await workspaces.SelectWorkspaceCommand.ExecuteAsync(source.Id);

        return (workspaces, workspaces.Settings.Workspaces.Single(workspace => workspace.Id == source.Id), target);
    }
}
