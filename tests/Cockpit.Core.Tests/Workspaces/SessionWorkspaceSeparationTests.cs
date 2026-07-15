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
