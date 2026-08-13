using System.Reflection;
using System.Text;
using Avalonia.Controls;
using Avalonia.Threading;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Sessions;
using Exclr8.Terminal;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-760: a held opening brief used to reach the pty the instant the process existed, before the hosted CLI was
/// actually reading stdin — landing in the composer with no submit. The fix rides the CLI's own readiness signal,
/// DECSET 2004, via <see cref="TerminalControl.BracketedPaste"/>; <c>TtyView._CheckHostedTuiReadiness</c> is the
/// gate. These pin it directly by reflecting into the view, the same way <c>TtyInjectedTextPasteTests</c> pins
/// <c>_WriteToPty</c>, rather than driving a real pty launch.
/// </summary>
[Collection("avalonia")]
public class TtyPromptReadinessTests
{
    private const string BracketedPasteStart = "\x1b[200~";
    private const string BracketedPasteEnd = "\x1b[201~";

    [Fact]
    public Task APaneWhosePtyExists_CannotTakeAPrompt_UntilBracketedPasteTurnsOn() =>
        HeadlessAvalonia.RunAsync(async () =>
        {
            var (_, viewModel, _) = _NewLaunchedView();

            // The pty process exists (PromptSink is wired, mirroring StartPty) but the hosted CLI has said nothing
            // yet — this is exactly the gap AC-760 reports.
            Assert.False(viewModel.CanTakeAPrompt);
        });

    [Fact]
    public Task AHeldBrief_IsNotWrittenToThePty_BeforeBracketedPasteTurnsOn() =>
        HeadlessAvalonia.RunAsync(async () =>
        {
            var (view, viewModel, pty) = _NewLaunchedView();

            var deliveredNow = viewModel.SubmitPromptWhenReady("review the migration");

            Assert.False(deliveredNow);
            Assert.True(viewModel.HasPromptWaitingToBeDelivered);
            Assert.Empty(pty.Written);

            _AnnounceBracketedPaste(view);
            // Only the terminal buffer's own flag changed so far — the readiness gate has not run yet, so the held
            // brief must still be sitting untouched.
            Assert.Empty(pty.Written);

            _InvokeCheckReadiness(view);
            await _LetPostedWorkRunAsync();

            Assert.True(viewModel.CanTakeAPrompt);
            Assert.False(viewModel.HasPromptWaitingToBeDelivered);
            var written = Encoding.UTF8.GetString(pty.Written.ToArray());
            Assert.Equal($"{BracketedPasteStart}review the migration{BracketedPasteEnd}\r", written);
        });

    [Fact]
    public Task TheFallbackDeadline_DeliversTheHeldBrief_IfBracketedPasteNeverArrives() =>
        HeadlessAvalonia.RunAsync(async () =>
        {
            var (view, viewModel, pty) = _NewLaunchedView();
            viewModel.SubmitPromptWhenReady("review the migration");

            // A CLI that never announces bracketed paste must not hold the brief forever — ponytail: the fallback is
            // a fixed 15s in production; here the clock is moved instead of the test waiting for real seconds.
            _SetFirstPtyOutputAt(view, DateTime.UtcNow - TimeSpan.FromSeconds(16));
            _InvokeCheckReadiness(view);
            await _LetPostedWorkRunAsync();

            Assert.True(viewModel.CanTakeAPrompt);
            Assert.False(viewModel.HasPromptWaitingToBeDelivered);
            Assert.NotEmpty(pty.Written);
        });

    [Fact]
    public Task TheFallbackDeadline_DoesNotFire_BeforeItHasElapsed() =>
        HeadlessAvalonia.RunAsync(async () =>
        {
            var (view, viewModel, pty) = _NewLaunchedView();
            viewModel.SubmitPromptWhenReady("review the migration");

            _SetFirstPtyOutputAt(view, DateTime.UtcNow - TimeSpan.FromSeconds(5));
            _InvokeCheckReadiness(view);

            Assert.False(viewModel.CanTakeAPrompt);
            Assert.Empty(pty.Written);
        });

    [Fact]
    public Task AHeldBrief_GoesOutExactlyOnce_HoweverOftenReadinessIsAnnounced() =>
        HeadlessAvalonia.RunAsync(async () =>
        {
            var (view, viewModel, pty) = _NewLaunchedView();
            viewModel.SubmitPromptWhenReady("review the migration");

            _AnnounceBracketedPaste(view);
            _InvokeCheckReadiness(view);
            _InvokeCheckReadiness(view);
            _InvokeCheckReadiness(view);
            await _LetPostedWorkRunAsync();

            var written = Encoding.UTF8.GetString(pty.Written.ToArray());
            Assert.Equal($"{BracketedPasteStart}review the migration{BracketedPasteEnd}\r", written);
        });

    [Fact]
    public Task RelaunchingThePane_ResetsReadiness_SoTheNewSessionIsNotTreatedAsAlreadyReady() =>
        HeadlessAvalonia.RunAsync(async () =>
        {
            var (view, viewModel, _) = _NewLaunchedView();
            _AnnounceBracketedPaste(view);
            _InvokeCheckReadiness(view);
            Assert.True(viewModel.CanTakeAPrompt);

            // What StartPty does before re-launching a reused pane (AC-564-style restart): the pty's own state is
            // cleared, and so must the readiness gate be — otherwise a stale "ready" from the session before would
            // let a brief for the new CLI process through before it has said anything at all.
            viewModel.ResetHostedTuiReadiness();

            Assert.False(viewModel.CanTakeAPrompt);
        });

    // Lets the pty write posted onto the UI thread by `_WriteToPty` actually run — see the identical helper in
    // `TtyInjectedTextPasteTests`.
    private static async Task _LetPostedWorkRunAsync() => await Dispatcher.UIThread.InvokeAsync(() => { });

    private static (TtyView View, TtyViewModel ViewModel, FakePty Pty) _NewLaunchedView()
    {
        var view = new TtyView();
        var window = new Window { Content = view, Width = 800, Height = 400 };
        window.Show();
        window.UpdateLayout();

        var viewModel = new TtyViewModel();
        // AC-64 schedules the submit CR a beat after the text; run it inline so delivery is assertable without a
        // real wait, matching TtyInjectedTextTests/AssistantSendGatewayTests.
        viewModel.SetAutoSubmitScheduler(submit => submit());
        view.DataContext = viewModel; // Triggers OnDataContextChanged -> WireTerminal(), same as production.

        var pty = new FakePty();
        typeof(TtyView).GetField("_pty", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(view, pty);

        // What TtyView.StartPty does the instant the pty process exists — before this test's readiness gate has
        // had any say, which is the whole point of AC-760.
        var writeToPty = typeof(TtyView)
            .GetMethod("_WriteToPty", BindingFlags.NonPublic | BindingFlags.Instance, [typeof(string)])!;
        viewModel.PromptSink = (Action<string>)Delegate.CreateDelegate(typeof(Action<string>), view, writeToPty);

        return (view, viewModel, pty);
    }

    private static void _AnnounceBracketedPaste(TtyView view) =>
        _Terminal(view).Write("\x1b[?2004h"u8.ToArray());

    private static void _InvokeCheckReadiness(TtyView view) =>
        typeof(TtyView).GetMethod("_CheckHostedTuiReadiness", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(view, null);

    private static void _SetFirstPtyOutputAt(TtyView view, DateTime whenUtc) =>
        typeof(TtyView).GetField("_firstPtyOutputAtUtc", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(view, (DateTime?)whenUtc);

    // The x:Name-generated field for the XAML-declared TerminalControl, not exposed publicly by TtyView.
    private static TerminalControl _Terminal(TtyView view) =>
        (TerminalControl)typeof(TtyView).GetField("Terminal", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(view)!;

    private sealed class FakePty : IConPtyProcess
    {
        private readonly MemoryStream _input = new();

        public Stream InputStream => _input;

        public Stream OutputStream { get; } = new MemoryStream();

        public int ProcessId => 0;

        public byte[] Written => _input.ToArray();

        public void Resize(short columns, short rows)
        {
        }

        public void Dispose() => _input.Dispose();
    }
}
