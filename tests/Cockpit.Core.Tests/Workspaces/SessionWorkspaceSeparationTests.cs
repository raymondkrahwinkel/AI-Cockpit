using Cockpit.App.ViewModels;
using Cockpit.Core.Workspaces;

namespace Cockpit.Core.Tests.Workspaces;

/// <summary>
/// Two Sessions workspaces are separate desks (Raymond, 2026-07-15: "ik heb nu 2 session workspaces gemaakt
/// maar beide tonen dezelfde claude nu, dus die zijn niet gescheiden?"). Each shows only its own sessions —
/// and, just as importantly, the others keep running: they are hidden, never removed from
/// <see cref="CockpitViewModel.Sessions"/>, because rebuilding a pane is what cost a TTY its pty on
/// 2026-07-13.
/// </summary>
public class SessionWorkspaceSeparationTests
{
    [Fact]
    public void ASessionOnAnotherWorkspace_IsHiddenFromTheGrid_ButStaysAlive()
    {
        var cockpit = _Create(out var workspaces);
        var first = workspaces.Active!;
        var mine = _AddSession(cockpit, first.Id);

        _SwitchToASecondWorkspace(workspaces);

        Assert.False(mine.IsPaneVisible, "the other desk's session is hidden, not closed");
        Assert.Contains(mine, cockpit.Sessions);
        Assert.DoesNotContain(mine, cockpit.VisibleSessions);
    }

    [Fact]
    public void EachWorkspace_ShowsOnlyItsOwnSessions()
    {
        var cockpit = _Create(out var workspaces);
        var first = workspaces.Active!;
        var onFirst = _AddSession(cockpit, first.Id);

        var second = _SwitchToASecondWorkspace(workspaces);
        var onSecond = _AddSession(cockpit, second.Id);

        Assert.Equal(onSecond, Assert.Single(cockpit.VisibleSessions));

        workspaces.SelectWorkspaceCommand.Execute(first.Id);

        Assert.Equal(onFirst, Assert.Single(cockpit.VisibleSessions));
    }

    [Fact]
    public void AFreshSecondWorkspace_GreetsYouWithTheEmptyState_EvenWhileTheFirstIsFull()
    {
        var cockpit = _Create(out var workspaces);
        _AddSession(cockpit, workspaces.Active!.Id);
        Assert.False(cockpit.ShowSessionEmptyState);

        _SwitchToASecondWorkspace(workspaces);

        Assert.False(cockpit.HasSessionsHere);
        Assert.True(cockpit.ShowSessionEmptyState);
        Assert.False(cockpit.ShowSessionGrid);
        Assert.True(cockpit.HasSessions, "the first workspace's session is still running");
    }

    [Fact]
    public void ASessionWithNoWorkspace_BelongsToTheFirstOne_SoNothingFromBeforeWorkspacesGoesMissing()
    {
        var cockpit = _Create(out var workspaces);
        var legacy = new SessionViewModel { Title = "From before workspaces" };
        cockpit.Sessions.Add(legacy);

        Assert.Contains(legacy, cockpit.VisibleSessions);

        _SwitchToASecondWorkspace(workspaces);

        Assert.DoesNotContain(legacy, cockpit.VisibleSessions);
    }

    [Fact]
    public void ADashboard_ShowsNoSessionsAtAll_AndNotTheSessionEmptyState()
    {
        var cockpit = _Create(out var workspaces);
        var mine = _AddSession(cockpit, workspaces.Active!.Id);

        workspaces.AddWorkspaceCommand.Execute(WorkspaceType.Dashboard);

        Assert.Empty(cockpit.VisibleSessions);
        Assert.False(mine.IsPaneVisible);
        Assert.False(cockpit.ShowSessionEmptyState, "a dashboard has its own empty state");
        Assert.False(cockpit.ShowSessionGrid);
    }

    [Fact]
    public void GridColumns_CountTheWorkspaceShowing_NotEverySessionAlive()
    {
        var cockpit = _Create(out var workspaces);
        var first = workspaces.Active!;
        _AddSession(cockpit, first.Id);
        _AddSession(cockpit, first.Id);
        Assert.Equal(2, cockpit.GridColumns);

        var second = _SwitchToASecondWorkspace(workspaces);
        _AddSession(cockpit, second.Id);

        Assert.Equal(1, cockpit.GridColumns);
        Assert.False(cockpit.ShowZoomButton);
    }

    [Fact]
    public async Task ClosingAWorkspace_StopsTheSessionsOnIt()
    {
        // They would otherwise keep running with a WorkspaceId pointing at a workspace that no longer exists:
        // no tab shows them, nothing can reach them, and their child process outlives the desk (Raymond).
        var cockpit = _Create(out var workspaces);
        var first = workspaces.Active!;
        var mine = _AddSession(cockpit, first.Id);
        var second = _SwitchToASecondWorkspace(workspaces);
        var survivor = _AddSession(cockpit, second.Id);

        await cockpit.CloseWorkspaceAsync(first.Id);

        Assert.DoesNotContain(mine, cockpit.Sessions);
        Assert.Contains(survivor, cockpit.Sessions);
        Assert.Equal(2, System.Linq.Enumerable.Count(workspaces.Settings.Workspaces));
        Assert.Equal(second.Id, workspaces.Settings.Workspaces.Single(workspace => workspace.Type == WorkspaceType.Sessions).Id);
    }

    [Fact]
    public async Task ClosingTheProjectsOverview_IsRefused_AndLeavesItsSessionsRunning()
    {
        // The one workspace closing can no longer take away: the fixed overview is what guarantees the cockpit
        // always has a desk to render, so unlike an ordinary Sessions workspace it stays un-closable regardless
        // of how many other workspaces exist. The one outcome worse than refusing: the desk survives and its
        // work does not — so a session elsewhere on the cockpit must be untouched by the refusal.
        var cockpit = _Create(out var workspaces);
        var sessions = workspaces.Active!;
        var session = _AddSession(cockpit, sessions.Id);
        var overview = workspaces.Settings.Workspaces.Single(workspace => workspace.Type == WorkspaceType.Projects);

        await cockpit.CloseWorkspaceAsync(overview.Id);

        Assert.Contains(session, cockpit.Sessions);
        Assert.Contains(workspaces.Settings.Workspaces, workspace => workspace.Id == overview.Id);
    }

    [Fact]
    public async Task ClosingADashboard_LeavesEverySessionAlone()
    {
        var cockpit = _Create(out var workspaces);
        var session = _AddSession(cockpit, workspaces.Active!.Id);
        await workspaces.AddWorkspaceCommand.ExecuteAsync(WorkspaceType.Dashboard);
        var dashboard = workspaces.Active!;

        await cockpit.CloseWorkspaceAsync(dashboard.Id);

        Assert.Contains(session, cockpit.Sessions);
    }

    private static CockpitViewModel _Create(out WorkspacesViewModel workspaces)
    {
        var cockpit = new CockpitViewModel();
        workspaces = cockpit.Workspaces;
        cockpit.Sessions.Clear();
        return cockpit;
    }

    /// <summary>Adds a session already stamped with its workspace — what <c>AddSession</c> does at runtime.</summary>
    private static SessionViewModel _AddSession(CockpitViewModel cockpit, string workspaceId)
    {
        var session = new SessionViewModel { Title = $"S{cockpit.Sessions.Count + 1}", WorkspaceId = workspaceId };
        cockpit.Sessions.Add(session);
        return session;
    }

    private static Workspace _SwitchToASecondWorkspace(WorkspacesViewModel workspaces)
    {
        workspaces.AddWorkspaceCommand.Execute(WorkspaceType.Sessions);
        return workspaces.Active!;
    }
}
