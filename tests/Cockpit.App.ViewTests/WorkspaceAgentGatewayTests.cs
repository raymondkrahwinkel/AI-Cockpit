using Avalonia.Threading;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Workspaces;

namespace Cockpit.App.ViewTests;

/// <summary>
/// <see cref="WorkspaceAgentGateway"/> is where workspace isolation for the agent-coordination line (AC-391) is
/// actually enforced: it decides which sessions share a workspace, and therefore which panes an agent's
/// <c>list_agents</c> call can ever see. The existing unit tests under
/// <c>Cockpit.Infrastructure.Tests/Agents</c> stub <c>IWorkspaceAgentGateway</c> with NSubstitute — they prove
/// <c>AgentsMcpTools</c> calls the gateway correctly, never that the gateway itself draws the boundary correctly.
/// That boundary can only be exercised against a real <see cref="CockpitViewModel"/> and its real
/// <see cref="SessionPanelViewModel"/> collection, which is why this lives here rather than in the unit tests —
/// the same reasoning as <see cref="PluginActionsSessionNameTests"/>, and for the same mechanical reason: building
/// a <see cref="SessionViewModel"/> and touching its observable properties has to happen on Avalonia's UI thread,
/// or the dispatcher-bound plumbing underneath it never settles.
/// </summary>
[Collection("avalonia")]
public class WorkspaceAgentGatewayTests
{
    [Fact]
    public void GetWorkspaceSnapshot_TwoWorkspaces_OnlyReturnsTheCallersOwnWorkspaceMates()
    {
        var (gateway, deskA, deskB) = Dispatcher.UIThread.Invoke(() =>
        {
            var cockpit = new CockpitViewModel();
            var sessionA = new SessionViewModel { WorkspaceId = "desk-a" };
            var sessionB = new SessionViewModel { WorkspaceId = "desk-b" };
            cockpit.Sessions.Add(sessionA);
            cockpit.Sessions.Add(sessionB);

            return (new WorkspaceAgentGateway(cockpit), sessionA, sessionB);
        });

        var snapshot = Dispatcher.UIThread.Invoke(() => gateway.GetWorkspaceSnapshot(deskA.PaneId));

        Assert.NotNull(snapshot);
        Assert.Equal("desk-a", snapshot!.WorkspaceId);
        Assert.Single(snapshot.Panes);
        Assert.Equal(deskA.PaneId, snapshot.Panes[0].PaneId);
        Assert.DoesNotContain(snapshot.Panes, pane => pane.PaneId == deskB.PaneId);
    }

    [Fact]
    public void GetWorkspaceSnapshot_TwoSessionsOnTheSameDesk_BothAppearInTheSnapshot()
    {
        var (gateway, sessionA, sessionB) = Dispatcher.UIThread.Invoke(() =>
        {
            var cockpit = new CockpitViewModel();
            var a = new SessionViewModel { WorkspaceId = "shared-desk" };
            var b = new SessionViewModel { WorkspaceId = "shared-desk" };
            cockpit.Sessions.Add(a);
            cockpit.Sessions.Add(b);

            return (new WorkspaceAgentGateway(cockpit), a, b);
        });

        var snapshot = Dispatcher.UIThread.Invoke(() => gateway.GetWorkspaceSnapshot(sessionA.PaneId));

        Assert.NotNull(snapshot);
        Assert.Equal(2, snapshot!.Panes.Count);
        Assert.Contains(snapshot.Panes, pane => pane.PaneId == sessionA.PaneId);
        Assert.Contains(snapshot.Panes, pane => pane.PaneId == sessionB.PaneId);
    }

    [Fact]
    public void GetWorkspaceSnapshot_UnknownPaneId_ReturnsNull()
    {
        var gateway = Dispatcher.UIThread.Invoke(() => new WorkspaceAgentGateway(new CockpitViewModel()));

        var snapshot = Dispatcher.UIThread.Invoke(() => gateway.GetWorkspaceSnapshot("no-such-pane"));

        Assert.Null(snapshot);
    }

    /// <summary>
    /// Mirrors the fallback <see cref="CockpitViewModel.BelongsToActiveWorkspace"/> uses: a session with no
    /// workspace stamp (created before workspaces existed, or in the design-time graph) belongs to the first
    /// Sessions workspace rather than to none. Two unstamped sessions must therefore land on each other, and not
    /// on a session explicitly stamped to a different, real workspace — that consistency is the whole point of
    /// the fallback, not just that it resolves to *something*.
    /// </summary>
    [Fact]
    public void GetWorkspaceSnapshot_SessionsWithNoWorkspaceStamp_FallBackToTheFirstSessionsWorkspaceTogether()
    {
        var (gateway, unstampedA, unstampedB, stampedElsewhere, firstSessionsWorkspaceId) = Dispatcher.UIThread.Invoke(() =>
        {
            var cockpit = new CockpitViewModel();
            var firstSessionsWorkspace = cockpit.Workspaces.Settings.Workspaces.First(workspace => workspace.Type == WorkspaceType.Sessions);
            var otherDesk = Workspace.Create("Other desk", WorkspaceType.Sessions);
            cockpit.Workspaces.Settings = cockpit.Workspaces.Settings.WithWorkspace(otherDesk);

            // No WorkspaceId set at all: both keep the type's default (empty string).
            var a = new SessionViewModel();
            var b = new SessionViewModel();
            var elsewhere = new SessionViewModel { WorkspaceId = otherDesk.Id };
            cockpit.Sessions.Add(a);
            cockpit.Sessions.Add(b);
            cockpit.Sessions.Add(elsewhere);

            return (new WorkspaceAgentGateway(cockpit), a, b, elsewhere, firstSessionsWorkspace.Id);
        });

        var snapshot = Dispatcher.UIThread.Invoke(() => gateway.GetWorkspaceSnapshot(unstampedA.PaneId));

        Assert.NotNull(snapshot);
        Assert.Equal(firstSessionsWorkspaceId, snapshot!.WorkspaceId);
        Assert.Contains(snapshot.Panes, pane => pane.PaneId == unstampedA.PaneId);
        Assert.Contains(snapshot.Panes, pane => pane.PaneId == unstampedB.PaneId);
        Assert.DoesNotContain(snapshot.Panes, pane => pane.PaneId == stampedElsewhere.PaneId);
    }

    [Fact]
    public void GetWorkspaceSnapshot_APaneThatIsNotARealAgentSession_NeverAppearsAsANeighbour()
    {
        var (gateway, agentSession, plainTerminal) = Dispatcher.UIThread.Invoke(() =>
        {
            var cockpit = new CockpitViewModel();
            var agent = new SessionViewModel { WorkspaceId = "desk-a" };
            var terminal = new SessionViewModel { WorkspaceId = "desk-a", ShowPluginHeaderItems = false };
            cockpit.Sessions.Add(agent);
            cockpit.Sessions.Add(terminal);

            return (new WorkspaceAgentGateway(cockpit), agent, terminal);
        });

        var snapshot = Dispatcher.UIThread.Invoke(() => gateway.GetWorkspaceSnapshot(agentSession.PaneId));

        Assert.NotNull(snapshot);
        Assert.Single(snapshot!.Panes);
        Assert.DoesNotContain(snapshot.Panes, pane => pane.PaneId == plainTerminal.PaneId);
    }
}
