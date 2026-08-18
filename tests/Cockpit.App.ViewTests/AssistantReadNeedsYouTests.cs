using Avalonia.Threading;
using Cockpit.App.Plugins;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;

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

    // No regression on the SDK route (AC6): its own flag still drives NeedsYou, untouched by the TTY arm added
    // alongside it.
    [Fact]
    public void ListSessions_ForAnSdkSessionWithAPendingPermission_StillReportsNeedsYou()
    {
        var (gateway, session) = Dispatcher.UIThread.Invoke(() =>
        {
            var cockpit = new CockpitViewModel();
            var sdk = new SessionViewModel();
            sdk.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.ToolUse, "Bash") { IsPendingPermission = true });
            cockpit.Sessions.Add(sdk);
            return (new AssistantReadGateway(cockpit, new SharedProjectSourceRegistry()), sdk);
        });

        var rows = Dispatcher.UIThread.Invoke(() => gateway.ListSessionsAsync().GetAwaiter().GetResult());

        var row = Assert.Single(rows, row => row.PaneId == session.PaneId);
        Assert.True(row.NeedsYou);
    }
}
