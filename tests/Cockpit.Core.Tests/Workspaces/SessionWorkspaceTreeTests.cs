using Cockpit.App.ViewModels;
using Cockpit.Core.Workspaces;

namespace Cockpit.Core.Tests.Workspaces;

/// <summary>
/// AC-1306: the panels sidebar's session list is a workspace tree — <see cref="CockpitViewModel.SessionWorkspaceGroups"/>.
/// It groups <em>where a session stands</em>: a workspace tab and the sessions placed on it. It is not AC-1302's
/// rail in the Simple stand, which groups <em>who drives what</em>.
/// </summary>
/// <remarks>
/// The flat list this replaces was filtered to the tab now showing, on the stated ground that the sidebar must
/// never offer a session the grid is hiding. The tree breaks that filter on purpose, so it has to pay the same
/// debt the other way round: picking a row on another tab walks to that tab first. That pair is one decision and
/// is tested as one — either half alone leaves the sidebar broken in a different way.
/// </remarks>
public class SessionWorkspaceTreeTests
{
    [Fact]
    public void TheTree_ListsASessionStandingOnATabThatIsNotShowing_UnderThatTabsOwnNode()
    {
        var (cockpit, here, there) = _TwoTabs();
        var mine = _AddSession(cockpit, here.Id);
        var theirs = _AddSession(cockpit, there.Id);

        var nodes = cockpit.SessionWorkspaceGroups.ToList();

        Assert.Equal([here.Id, there.Id], nodes.Select(node => node.Id));
        Assert.Equal([mine], nodes[0].Sessions);
        Assert.Equal([theirs], nodes[1].Sessions);

        // The flat list still shows only what the grid can: the tree is the wider view, not a wider grid.
        Assert.Equal([mine], cockpit.VisibleSessions);
    }

    [Fact]
    public void PickingASessionOnAnotherTab_WalksToThatTab_SoTheGridCanShowWhatTheSidebarOffered()
    {
        var (cockpit, here, there) = _TwoTabs();
        _AddSession(cockpit, here.Id);
        var theirs = _AddSession(cockpit, there.Id);

        cockpit.SelectSessionCommand.Execute(theirs);

        Assert.Equal(there.Id, cockpit.Workspaces.Active!.Id);
        Assert.Contains(theirs, cockpit.VisibleSessions);
        Assert.True(theirs.IsPaneVisible, "the grid has to be showing the session the sidebar just handed over");

        // Criterion 2(a): the strip above the grid is the other half of the same switch and has to say so too.
        Assert.Equal(there.Id, cockpit.Workspaces.Tabs.Single(tab => tab.IsActive).Id);
    }

    [Fact]
    public void AnEmptyTab_DropsOutOfTheTreeAndIsCountedUnderIt_UnlessItIsTheOneYouAreStandingOn()
    {
        var (cockpit, here, there) = _TwoTabs();
        _AddSession(cockpit, here.Id);

        // `there` is empty and not showing: left out, and said to be left out.
        Assert.Equal([here.Id], cockpit.SessionWorkspaceGroups.Select(node => node.Id));
        Assert.Equal(1, cockpit.OmittedEmptyWorkspaceCount);
        Assert.True(cockpit.HasOmittedEmptyWorkspaces);

        cockpit.Workspaces.SelectWorkspaceCommand.Execute(there.Id);

        // Now it is the one being stood on, so it keeps its node even holding nothing — else the tree stops
        // pointing at where you are, exactly when you have just walked there.
        var nodes = cockpit.SessionWorkspaceGroups.ToList();
        Assert.Equal([here.Id, there.Id], nodes.Select(node => node.Id));
        Assert.Empty(nodes[1].Sessions);
        Assert.True(nodes[1].ShowEmptyNote);
        Assert.True(nodes[1].IsActive);
        Assert.Equal(0, cockpit.OmittedEmptyWorkspaceCount);

        // A Projects tab cannot hold a session at all, so it is never a node — the strip is the only way to it,
        // which is half of why the strip stays.
        Assert.DoesNotContain(
            cockpit.Workspaces.Settings.Workspaces.Single(workspace => workspace.Type == WorkspaceType.Projects).Id,
            nodes.Select(node => node.Id));
    }

    [Fact]
    public void TheChevron_FoldsANodeWithoutWalkingToIt_AndWalkingToItUnfoldsItAgain()
    {
        var (cockpit, here, there) = _TwoTabs();
        _AddSession(cockpit, here.Id);
        _AddSession(cockpit, there.Id);

        cockpit.Workspaces.ToggleSidebarCollapsedCommand.Execute(there.Id);

        // Criterion 3(a): folding a tab away is tidying, not navigating.
        Assert.False(cockpit.SessionWorkspaceGroups.Single(node => node.Id == there.Id).IsExpanded);
        Assert.Equal(here.Id, cockpit.Workspaces.Active!.Id);

        cockpit.Workspaces.SelectWorkspaceCommand.Execute(there.Id);

        // Criterion 2(b): the strip switched, so the node follows — and a folded node would hide precisely the
        // sessions just switched to.
        Assert.True(cockpit.SessionWorkspaceGroups.Single(node => node.Id == there.Id).IsExpanded);
    }

    [Fact]
    public void TheMeasureLine_SaysNothing_UnlessSomethingIsLeftBehindOrTheMemoryIsWorthTheRow()
    {
        var quiet = new SessionViewModel { ProcessCount = 1, ProcessCpuPercent = 0, ProcessMemoryBytes = 90L * 1024 * 1024 };
        var heavy = new SessionViewModel { ProcessCount = 1, ProcessCpuPercent = 0, ProcessMemoryBytes = 1019L * 1024 * 1024 };
        var abandoned = new SessionViewModel { ProcessCount = 1, AbandonedProcessCount = 1, ProcessMemoryBytes = 4L * 1024 * 1024 };

        // No line at all rather than an empty one: the row is shorter for it, and the pixels are the point.
        Assert.Equal(string.Empty, quiet.ProcessActivityLabel);
        Assert.Equal("0% · 1019 MB", heavy.ProcessActivityLabel);
        Assert.StartsWith("1 left behind", abandoned.ProcessActivityLabel);
    }

    private static (CockpitViewModel Cockpit, Workspace Here, Workspace There) _TwoTabs()
    {
        var cockpit = new CockpitViewModel();
        cockpit.Sessions.Clear();
        var here = cockpit.Workspaces.Active!;
        cockpit.Workspaces.AddWorkspaceCommand.Execute(WorkspaceType.Sessions);
        var there = cockpit.Workspaces.Active!;
        cockpit.Workspaces.SelectWorkspaceCommand.Execute(here.Id);
        return (cockpit, here, there);
    }

    private static SessionViewModel _AddSession(CockpitViewModel cockpit, string workspaceId)
    {
        var session = new SessionViewModel { Title = $"S{cockpit.Sessions.Count + 1}", WorkspaceId = workspaceId };
        cockpit.Sessions.Add(session);
        return session;
    }
}
