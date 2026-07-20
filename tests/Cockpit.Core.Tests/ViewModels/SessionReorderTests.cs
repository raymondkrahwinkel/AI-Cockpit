using Cockpit.App.ViewModels;
using Cockpit.Core.Workspaces;
using FluentAssertions;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// Reordering sessions in the sidebar (AC-115): the drag-reorder and the Move up/down menu items share one
/// primitive, <see cref="CockpitViewModel.MoveSessionToVisibleIndex"/>. Order lives only in the in-memory
/// <see cref="CockpitViewModel.Sessions"/> collection — sessions are not persisted across a restart, so the
/// order isn't either, by design for AC-115. The subtlety these tests pin down: the sidebar shows a
/// per-workspace filtered <see cref="CockpitViewModel.VisibleSessions"/>, so a reorder must be relative to the
/// visible rows, not to the global collection that can interleave other workspaces' sessions.
/// </summary>
public class SessionReorderTests
{
    [Fact]
    public void MoveSessionToVisibleIndex_MovesTheSessionToTheTargetPosition()
    {
        var cockpit = _Create(out var workspaces);
        var a = _AddSession(cockpit, workspaces.Active!.Id);
        var b = _AddSession(cockpit, workspaces.Active!.Id);
        var c = _AddSession(cockpit, workspaces.Active!.Id);

        cockpit.MoveSessionToVisibleIndex(a, 2);

        cockpit.VisibleSessions.Should().Equal(b, c, a);
    }

    [Fact]
    public void MoveSessionToVisibleIndex_IsRelativeToTheVisibleRows_NotTheGlobalCollection()
    {
        // Global Sessions interleaves the two desks: [onFirst1, onSecond, onFirst2]. A move on the first desk
        // must anchor to the visible rows ([onFirst1, onFirst2]) and leave the other desk's session untouched.
        var cockpit = _Create(out var workspaces);
        var first = workspaces.Active!;
        var onFirst1 = _AddSession(cockpit, first.Id);
        var second = _SwitchToASecondWorkspace(workspaces);
        var onSecond = _AddSession(cockpit, second.Id);
        workspaces.SelectWorkspaceCommand.Execute(first.Id);
        var onFirst2 = _AddSession(cockpit, first.Id);

        cockpit.MoveSessionToVisibleIndex(onFirst1, 1);

        cockpit.VisibleSessions.Should().Equal(onFirst2, onFirst1);
        cockpit.Sessions.Should().Contain(onSecond, "the other desk's session is none of this reorder's business");
    }

    [Fact]
    public void MoveSessionUp_StepsPastAHiddenOtherWorkspaceSession()
    {
        // The bug the visible-relative move fixes: with [onFirst1, onSecond, onFirst2] in the global collection,
        // a raw index-1 "move up" on onFirst2 would swap it with onSecond — hidden on another desk — and change
        // nothing the operator can see. It must step past to sit before onFirst1.
        var cockpit = _Create(out var workspaces);
        var first = workspaces.Active!;
        var onFirst1 = _AddSession(cockpit, first.Id);
        var second = _SwitchToASecondWorkspace(workspaces);
        _AddSession(cockpit, second.Id);
        workspaces.SelectWorkspaceCommand.Execute(first.Id);
        var onFirst2 = _AddSession(cockpit, first.Id);

        cockpit.MoveSessionUpCommand.Execute(onFirst2);

        cockpit.VisibleSessions.Should().Equal(onFirst2, onFirst1);
    }

    [Fact]
    public void MoveSessionDown_StepsPastAHiddenOtherWorkspaceSession()
    {
        var cockpit = _Create(out var workspaces);
        var first = workspaces.Active!;
        var onFirst1 = _AddSession(cockpit, first.Id);
        var second = _SwitchToASecondWorkspace(workspaces);
        _AddSession(cockpit, second.Id);
        workspaces.SelectWorkspaceCommand.Execute(first.Id);
        var onFirst2 = _AddSession(cockpit, first.Id);

        cockpit.MoveSessionDownCommand.Execute(onFirst1);

        cockpit.VisibleSessions.Should().Equal(onFirst2, onFirst1);
    }

    [Fact]
    public void MoveSessionUp_OnTheFirstVisibleRow_DoesNothing()
    {
        var cockpit = _Create(out var workspaces);
        var a = _AddSession(cockpit, workspaces.Active!.Id);
        var b = _AddSession(cockpit, workspaces.Active!.Id);

        cockpit.MoveSessionUpCommand.Execute(a);

        cockpit.VisibleSessions.Should().Equal(a, b);
    }

    [Fact]
    public void MoveSessionToVisibleIndex_OutOfRangeOrSamePosition_DoesNothing()
    {
        var cockpit = _Create(out var workspaces);
        var a = _AddSession(cockpit, workspaces.Active!.Id);
        var b = _AddSession(cockpit, workspaces.Active!.Id);

        cockpit.MoveSessionToVisibleIndex(a, 0);
        cockpit.MoveSessionToVisibleIndex(a, 5);
        cockpit.MoveSessionToVisibleIndex(a, -1);

        cockpit.VisibleSessions.Should().Equal(a, b);
    }

    private static CockpitViewModel _Create(out WorkspacesViewModel workspaces)
    {
        var cockpit = new CockpitViewModel();
        workspaces = cockpit.Workspaces;
        cockpit.Sessions.Clear();
        return cockpit;
    }

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
