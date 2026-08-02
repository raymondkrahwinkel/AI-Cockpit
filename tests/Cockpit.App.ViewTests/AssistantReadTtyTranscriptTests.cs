using Avalonia.Threading;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-609: <c>read_transcript</c> over a TTY session.
/// <para>
/// The gateway used to answer only for <see cref="SessionViewModel"/> — the SDK kind — and a pane id naming
/// anything else fell out as "no AI session is running on this pane". TTY is how most of this cockpit's sessions
/// run, so that answer contradicted the <c>list_sessions</c> the caller had just read, which reported the same
/// pane as a live session with a current statusline. Two of the assistant's own read surfaces disagreeing about
/// whether a session exists is worse than either being merely incomplete: there is no way to tell which one is
/// lying from the outside.
/// </para>
/// </summary>
[Collection("avalonia")]
public class AssistantReadTtyTranscriptTests
{
    [Fact]
    public async Task ReadTranscriptAsync_ForATtySession_AnswersInsteadOfDenyingTheSessionExists()
    {
        var (gateway, tty) = Dispatcher.UIThread.Invoke(() =>
        {
            var cockpit = new CockpitViewModel();
            var session = new TtyViewModel();
            cockpit.Sessions.Add(session);
            return (new AssistantReadGateway(cockpit), session);
        });

        var transcript = await gateway.ReadTranscriptAsync(tty.PaneId, count: 30);

        // A session with nothing readable yet still answers as a session. Its emptiness is a fact about what it has
        // written — the caller can see that from totalEntries — not a claim that the pane is gone.
        Assert.NotNull(transcript);
        Assert.Equal(tty.PaneId, transcript!.PaneId);
    }

    [Fact]
    public async Task ReadTranscriptAsync_ForAPlainTerminal_StillReportsNoSession()
    {
        // The other half, and the reason the old type test looked right: a shell pane carries a pane id but has no
        // agent behind it to have said anything. "No AI session" is true of it, and must stay the answer.
        var (gateway, terminal) = Dispatcher.UIThread.Invoke(() =>
        {
            var cockpit = new CockpitViewModel();
            var session = TtyViewModel.DesignTerminal();
            cockpit.Sessions.Add(session);
            return (new AssistantReadGateway(cockpit), session);
        });

        Assert.Null(await gateway.ReadTranscriptAsync(terminal.PaneId, count: 30));
    }
}
