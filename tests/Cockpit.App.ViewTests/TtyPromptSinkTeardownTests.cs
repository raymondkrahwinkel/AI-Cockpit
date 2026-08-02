using Avalonia.Threading;
using Cockpit.App.ViewModels;

namespace Cockpit.App.ViewTests;

/// <summary>
/// A terminal pane that is gone says it cannot take a prompt — so nothing reports a turn as delivered into it.
/// </summary>
/// <remarks>
/// <c>PromptSink</c> points at the view's writer into the pty. Once the pty is disposed that writer returns without
/// a word, so a pane left holding the sink kept answering <c>CanTakeAPrompt: true</c>, took the prompt, dropped it,
/// and let <c>send_prompt</c> come back <c>delivered:true</c> — the model then telling the operator out loud that the
/// agent had started. Measured as <c>canTakeBefore=True canTakeAfterDispose=True submitReportedSent=True</c>.
/// <para>
/// The same delegate-outlives-its-target shape AC-226 settled for <c>PasteTextAsync</c>, which is cleared on exactly
/// these paths and for exactly this reason; the prompt route simply was not cleared with it.
/// </para>
/// </remarks>
[Collection("avalonia")]
public class TtyPromptSinkTeardownTests
{
    [Fact]
    public async Task ADisposedTerminalPane_CannotTakeAPrompt_AndDoesNotReportOneAsDelivered()
    {
        var (session, wroteToPty) = Dispatcher.UIThread.Invoke(() =>
        {
            var written = new List<string>();
            var tty = new TtyViewModel();

            // What TtyView does once the pty is up: hands the pane the route into its stdin.
            tty.PromptSink = written.Add;
            return (tty, written);
        });

        // The pane really can take one while it is alive — otherwise the assertions after the dispose would hold on
        // a pane that never worked at all.
        Assert.True(session.CanTakeAPrompt);

        await Dispatcher.UIThread.InvokeAsync(async () => await session.DisposeAsync());

        Assert.False(session.CanTakeAPrompt);

        // And the caller is told no rather than yes: held, not delivered, so nothing announces a turn that went
        // into a closed process.
        Assert.False(Dispatcher.UIThread.Invoke(() => session.SubmitPromptWhenReady("run the tests")));
        Assert.Empty(wroteToPty);
    }
}
