using Cockpit.App.ViewModels;
using Cockpit.Core.Workspaces;

namespace Cockpit.Core.Tests.Workspaces;

/// <summary>
/// AC-674: moving a running session's context-menu "Move to workspace" from one Sessions desk to another. Mirrors
/// <see cref="MoveWidgetBetweenWorkspacesTests"/> — this is the pane-record half of the move, exercised without a
/// live <c>SessionPanelViewModel</c> behind it, the same way the widget tests exercise it without a live widget.
/// </summary>
public class MoveSessionBetweenWorkspacesTests
{
    [Fact]
    public async Task ASessionPane_MovesToTheWorkspaceItWasDroppedOn()
    {
        var (workspaces, source, target) = await _TwoSessionsWorkspacesAsync();
        var pane = _Workspace(workspaces, source.Id).Panes.Single();

        var moved = await workspaces.MoveSessionPaneToWorkspaceAsync(source.Id, pane.Id, target.Id);

        Assert.True(moved);
        Assert.Empty(_Workspace(workspaces, source.Id).Panes);
        Assert.Equal(pane.Id, Assert.Single(_Workspace(workspaces, target.Id).Panes).Id);
    }

    /// <summary>The pane keeps its id — session state (worktree, conversation) is keyed by it, and a rebuilt pane loses the pty (leermoment 2026-07-13).</summary>
    [Fact]
    public async Task AMovedSessionPane_KeepsTheIdItsStateIsKeyedBy()
    {
        var (workspaces, source, target) = await _TwoSessionsWorkspacesAsync();
        var paneId = _Workspace(workspaces, source.Id).Panes.Single().Id;

        await workspaces.MoveSessionPaneToWorkspaceAsync(source.Id, paneId, target.Id);

        Assert.Equal(paneId, _Workspace(workspaces, target.Id).Panes.Single().Id);
    }

    [Fact]
    public async Task ASessionPane_DoesNotMoveToADashboard()
    {
        var (workspaces, source, _) = await _TwoSessionsWorkspacesAsync();
        await workspaces.AddWorkspaceCommand.ExecuteAsync(WorkspaceType.Dashboard);
        var dashboard = workspaces.Active!;
        var paneId = _Workspace(workspaces, source.Id).Panes.Single().Id;

        var moved = await workspaces.MoveSessionPaneToWorkspaceAsync(source.Id, paneId, dashboard.Id);

        Assert.False(moved);
        Assert.Single(_Workspace(workspaces, source.Id).Panes);
    }

    [Fact]
    public async Task ASessionPane_DroppedOnItsOwnWorkspace_DoesNothing()
    {
        var (workspaces, source, _) = await _TwoSessionsWorkspacesAsync();
        var paneId = _Workspace(workspaces, source.Id).Panes.Single().Id;

        var moved = await workspaces.MoveSessionPaneToWorkspaceAsync(source.Id, paneId, source.Id);

        Assert.False(moved);
        Assert.Single(_Workspace(workspaces, source.Id).Panes);
    }

    [Fact]
    public async Task AnUnknownPane_IsRefusedRatherThanThrowing()
    {
        var (workspaces, source, target) = await _TwoSessionsWorkspacesAsync();

        var moved = await workspaces.MoveSessionPaneToWorkspaceAsync(source.Id, "no-such-pane", target.Id);

        Assert.False(moved);
    }

    private static Workspace _Workspace(WorkspacesViewModel workspaces, string id) =>
        workspaces.Settings.Workspaces.Single(workspace => workspace.Id == id);

    /// <summary>Two Sessions desks, the first holding one AI-session pane.</summary>
    private static async Task<(WorkspacesViewModel Workspaces, Workspace Source, Workspace Target)> _TwoSessionsWorkspacesAsync()
    {
        var workspaces = new WorkspacesViewModel();
        var sourceId = workspaces.EnsureSessionWorkspace();
        await workspaces.AddPaneAsync(sourceId, new WorkspacePane(Guid.NewGuid().ToString("n"), PaneKind.AiSession));

        var target = await workspaces.CreateSessionsWorkspaceAsync("Second desk");
        await workspaces.SelectWorkspaceCommand.ExecuteAsync(sourceId);

        return (workspaces, workspaces.Settings.Workspaces.Single(workspace => workspace.Id == sourceId), target);
    }
}
