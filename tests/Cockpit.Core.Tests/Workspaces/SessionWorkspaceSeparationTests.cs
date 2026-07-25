using Cockpit.App.ViewModels;
using Cockpit.Core.Workspaces;
using FluentAssertions;

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

        mine.IsPaneVisible.Should().BeFalse("the other desk's session is hidden, not closed");
        cockpit.Sessions.Should().Contain(mine, "hiding a session must never remove it — its pty keeps running");
        cockpit.VisibleSessions.Should().NotContain(mine);
    }

    [Fact]
    public void EachWorkspace_ShowsOnlyItsOwnSessions()
    {
        var cockpit = _Create(out var workspaces);
        var first = workspaces.Active!;
        var onFirst = _AddSession(cockpit, first.Id);

        var second = _SwitchToASecondWorkspace(workspaces);
        var onSecond = _AddSession(cockpit, second.Id);

        cockpit.VisibleSessions.Should().ContainSingle().Which.Should().Be(onSecond);

        workspaces.SelectWorkspaceCommand.Execute(first.Id);

        cockpit.VisibleSessions.Should().ContainSingle().Which.Should().Be(onFirst);
    }

    [Fact]
    public void AFreshSecondWorkspace_GreetsYouWithTheEmptyState_EvenWhileTheFirstIsFull()
    {
        var cockpit = _Create(out var workspaces);
        _AddSession(cockpit, workspaces.Active!.Id);
        cockpit.ShowSessionEmptyState.Should().BeFalse();

        _SwitchToASecondWorkspace(workspaces);

        cockpit.HasSessionsHere.Should().BeFalse();
        cockpit.ShowSessionEmptyState.Should().BeTrue();
        cockpit.ShowSessionGrid.Should().BeFalse();
        cockpit.HasSessions.Should().BeTrue("the first workspace's session is still running");
    }

    [Fact]
    public void ASessionWithNoWorkspace_BelongsToTheFirstOne_SoNothingFromBeforeWorkspacesGoesMissing()
    {
        var cockpit = _Create(out var workspaces);
        var legacy = new SessionViewModel { Title = "From before workspaces" };
        cockpit.Sessions.Add(legacy);

        cockpit.VisibleSessions.Should().Contain(legacy);

        _SwitchToASecondWorkspace(workspaces);

        cockpit.VisibleSessions.Should().NotContain(legacy);
    }

    [Fact]
    public void ADashboard_ShowsNoSessionsAtAll_AndNotTheSessionEmptyState()
    {
        var cockpit = _Create(out var workspaces);
        var mine = _AddSession(cockpit, workspaces.Active!.Id);

        workspaces.AddWorkspaceCommand.Execute(WorkspaceType.Dashboard);

        cockpit.VisibleSessions.Should().BeEmpty();
        mine.IsPaneVisible.Should().BeFalse();
        cockpit.ShowSessionEmptyState.Should().BeFalse("a dashboard has its own empty state");
        cockpit.ShowSessionGrid.Should().BeFalse();
    }

    [Fact]
    public void GridColumns_CountTheWorkspaceShowing_NotEverySessionAlive()
    {
        var cockpit = _Create(out var workspaces);
        var first = workspaces.Active!;
        _AddSession(cockpit, first.Id);
        _AddSession(cockpit, first.Id);
        cockpit.GridColumns.Should().Be(2);

        var second = _SwitchToASecondWorkspace(workspaces);
        _AddSession(cockpit, second.Id);

        cockpit.GridColumns.Should().Be(1, "one session on this desk lays out as one, however full the other is");
        cockpit.ShowZoomButton.Should().BeFalse();
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

        cockpit.Sessions.Should().NotContain(mine);
        cockpit.Sessions.Should().Contain(survivor, "the other desk's session is none of this workspace's business");
        workspaces.Settings.Workspaces.Should().HaveCount(2, "the survivor, plus the fixed overview that was there all along");
        workspaces.Settings.Workspaces.Single(workspace => workspace.Type == WorkspaceType.Sessions).Id.Should().Be(second.Id);
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

        cockpit.Sessions.Should().Contain(session);
        workspaces.Settings.Workspaces.Should().Contain(workspace => workspace.Id == overview.Id);
    }

    [Fact]
    public async Task ClosingADashboard_LeavesEverySessionAlone()
    {
        var cockpit = _Create(out var workspaces);
        var session = _AddSession(cockpit, workspaces.Active!.Id);
        await workspaces.AddWorkspaceCommand.ExecuteAsync(WorkspaceType.Dashboard);
        var dashboard = workspaces.Active!;

        await cockpit.CloseWorkspaceAsync(dashboard.Id);

        cockpit.Sessions.Should().Contain(session);
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
