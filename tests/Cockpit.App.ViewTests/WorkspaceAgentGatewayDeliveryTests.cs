using Avalonia.Threading;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The roster reports each pane's own answer to "will a message sent here surface on its own" (AC-394), rather than
/// deciding it for them.
/// <para>
/// Worth a test of its own because it is the one link in that chain nothing else covers: the tool-layer tests build
/// <c>WorkspaceAgentPane</c> records by hand, so a gateway that reported every pane as delivering — or none — would
/// leave all of them green while telling every sender on the desk the opposite of the truth.
/// </para>
/// </summary>
public class WorkspaceAgentGatewayDeliveryTests
{
    [Fact]
    public async Task TheRoster_TakesEachPanesOwnAnswerOnPassiveDelivery()
    {
        var (cockpit, session, terminal) = Dispatcher.UIThread.Invoke(() =>
        {
            var cockpit = new CockpitViewModel();

            // Both are agent panes sharing a desk — the terminal one is a CLI running in a pty, not a plain shell, so
            // it is on the roster like any other. That is exactly the pane this ticket cannot deliver to.
            var session = new SessionViewModel();
            var terminal = new TtyViewModel();
            cockpit.Sessions.Add(session);
            cockpit.Sessions.Add(terminal);
            return (cockpit, session, terminal);
        });

        var snapshot = await new WorkspaceAgentGateway(cockpit).GetWorkspaceSnapshotAsync(session.PaneId);

        Assert.NotNull(snapshot);
        Assert.True(snapshot.Panes.Single(pane => pane.PaneId == session.PaneId).DeliversAtTurnStart);
        Assert.False(snapshot.Panes.Single(pane => pane.PaneId == terminal.PaneId).DeliversAtTurnStart);
    }
}
