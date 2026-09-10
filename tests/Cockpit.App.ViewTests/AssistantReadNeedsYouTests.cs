using Avalonia.Threading;
using Cockpit.App.Plugins;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Sessions;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-920: <c>list_sessions</c> reports <c>NeedsYou</c> for a TTY pane with an open <c>AskUserQuestion</c>
/// prompt, the same way it already does for an SDK session's pending permission — a blocked terminal pane used
/// to read exactly like a busy one.
/// </summary>
[Collection("avalonia")]
public class AssistantReadNeedsYouTests
{
    [Fact]
    public void ListSessions_ForATtyPaneAwaitingAnAnswer_ReportsNeedsYou()
    {
        var (gateway, tty) = Dispatcher.UIThread.Invoke(() =>
        {
            var cockpit = new CockpitViewModel();
            var session = new TtyViewModel { SessionStatus = SessionStatus.NeedsAttention };
            cockpit.Sessions.Add(session);
            return (new AssistantReadGateway(cockpit, new SharedProjectSourceRegistry()), session);
        });

        var rows = Dispatcher.UIThread.Invoke(() => gateway.ListSessionsAsync().GetAwaiter().GetResult());

        var row = Assert.Single(rows, row => row.PaneId == tty.PaneId);
        Assert.True(row.NeedsYou);
    }

    [Fact]
    public void ListSessions_ForATtyPaneWithNoOpenPrompt_ReportsNotNeedsYou()
    {
        var (gateway, tty) = Dispatcher.UIThread.Invoke(() =>
        {
            var cockpit = new CockpitViewModel();
            var session = new TtyViewModel { SessionStatus = SessionStatus.Busy };
            cockpit.Sessions.Add(session);
            return (new AssistantReadGateway(cockpit, new SharedProjectSourceRegistry()), session);
        });

        var rows = Dispatcher.UIThread.Invoke(() => gateway.ListSessionsAsync().GetAwaiter().GetResult());

        var row = Assert.Single(rows, row => row.PaneId == tty.PaneId);
        Assert.False(row.NeedsYou);
    }

    // AC-1309: NeedsYou is derived from Status alone now — one source of truth instead of a second reading of
    // `HasPendingPermission`. That widens what counts: a CLI `needs_action` with no pending-permission row (no
    // tool call to answer, just a status signal) used to read as NeedsYou=false on the SDK route; it does not now.
    [Fact]
    public void ListSessions_ForAnSdkSessionWithACliNeedsAction_ReportsNeedsYou_WithNoPendingPermissionRow()
    {
        var (gateway, session) = Dispatcher.UIThread.Invoke(() =>
        {
            var cockpit = new CockpitViewModel();
            // The parameterless constructor is the previewer's — it seeds sample transcript rows, including one
            // with IsPendingPermission set, which would make this assertion meaningless either way.
            var sdk = new SessionViewModel(Substitute.For<ISessionManager>());
            sdk.Apply(new SessionStatusChanged { SessionId = "s1", NeedsAction = "needs_action" });
            cockpit.Sessions.Add(sdk);
            return (new AssistantReadGateway(cockpit, new SharedProjectSourceRegistry()), sdk);
        });

        var rows = Dispatcher.UIThread.Invoke(() => gateway.ListSessionsAsync().GetAwaiter().GetResult());

        var row = Assert.Single(rows, row => row.PaneId == session.PaneId);
        Assert.True(row.NeedsYou);
        Assert.False(session.HasPendingPermission, "the old check would have missed this — no permission row exists here");
    }
}
