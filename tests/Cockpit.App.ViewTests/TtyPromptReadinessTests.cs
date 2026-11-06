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
/// AC-760: a held brief used to reach the pty the instant the process existed, before the CLI actually read
/// stdin — landing in the composer with no submit. The fix rides DECSET 2004 via
/// <see cref="TerminalControl.BracketedPaste"/>, gated by <c>TtyView._CheckHostedTuiReadiness</c>; these pin it by
/// reflecting into the view, like <c>TtyInjectedTextPasteTests</c> does, rather than driving a real pty launch.
/// </summary>
[Collection("avalonia")]
public class TtyPromptReadinessTests
{
    private const string BracketedPasteStart = "\x1b[200~";
    private const string BracketedPasteEnd = "\x1b[201~";

    // D: Claude's native trust menu accepts input before its composer does (AC-2164).
    [Fact]
    public Task ClaudeTrust_HoldsTheBrief_AndRefusesDirectSend_UntilTheComposerAppears() =>
        HeadlessAvalonia.RunAsync(async () =>
        {
            var (view, viewModel, pty) = _NewLaunchedView();

            _Terminal(view).Write(Encoding.UTF8.GetBytes("Do you trust the files in this folder?\r\n❯ 1. Yes, I trust this folder\r\n  2. No, exit"));
            _AnnounceBracketedPaste(view);
            _SetFirstPtyOutputAt(view, DateTime.UtcNow - TimeSpan.FromMinutes(1));
            _InvokeCheckReadiness(view);
            Assert.False(await viewModel.SendPromptAsync("must not answer trust"));
            Assert.Equal(SessionStatus.NeedsAttention, viewModel.SessionStatus);

            var deliveredNow = viewModel.SubmitPromptWhenReady("review the migration");

            Assert.False(deliveredNow);
            Assert.True(viewModel.HasPromptWaitingToBeDelivered);
            Assert.Empty(pty.Written);

            _Terminal(view).Write(Encoding.UTF8.GetBytes("\x1b[2J\x1b[HChoose the text style\r\n❯ ✔ Dark mode\r\n  Light mode"));
            _InvokeCheckReadiness(view);
            Assert.False(viewModel.CanTakeAPrompt);
            Assert.True(viewModel.HasPromptWaitingToBeDelivered);
            Assert.Empty(pty.Written);

            _Terminal(view).Write(Encoding.UTF8.GetBytes("\x1b[2J\x1b[H❯ restored draft"));
            _InvokeCheckReadiness(view);
            await _LetPostedWorkRunAsync();

            Assert.True(viewModel.CanTakeAPrompt);
            Assert.False(viewModel.HasPromptWaitingToBeDelivered);
            var written = Encoding.UTF8.GetString(pty.Written.ToArray());
            Assert.Equal($"{BracketedPasteStart}review the migration{BracketedPasteEnd}\r", written);
        });

    // A CLI that never announces bracketed paste must not hold the brief forever — and must not have it taken off
    // it early either. The fallback is a fixed 15s in production; here the clock is moved rather than waited out.
    [Theory]
    [InlineData(16, true)]
    [InlineData(5, false)]
    public Task TheFallbackDeadline_DeliversTheHeldBrief_OnlyOnceItHasElapsed(int secondsSinceFirstOutput, bool elapsed) =>
        HeadlessAvalonia.RunAsync(async () =>
        {
            var (view, viewModel, pty) = _NewLaunchedView();
            viewModel.SubmitPromptWhenReady("review the migration");

            _SetFirstPtyOutputAt(view, DateTime.UtcNow - TimeSpan.FromSeconds(secondsSinceFirstOutput));
            _InvokeCheckReadiness(view);
            await _LetPostedWorkRunAsync();

            Assert.Equal(elapsed, viewModel.CanTakeAPrompt);
            Assert.Equal(elapsed, !viewModel.HasPromptWaitingToBeDelivered);
            Assert.Equal(elapsed, pty.Written.Length > 0);
        });

    [Fact]
    public Task CodexTrust_HoldsTheBrief_AndDeliversExactlyOnce_WhenTheComposerAppears() =>
        HeadlessAvalonia.RunAsync(async () =>
        {
            var (view, viewModel, pty) = _NewLaunchedView();
            viewModel.SubmitPromptWhenReady("review the migration");

            _AnnounceBracketedPaste(view);
            _Terminal(view).Write(Encoding.UTF8.GetBytes("Do you trust the contents of this directory?\r\n› 1. Yes, continue"));
            _SetFirstPtyOutputAt(view, DateTime.UtcNow - TimeSpan.FromMinutes(1));
            _InvokeCheckReadiness(view);
            Assert.False(viewModel.CanTakeAPrompt);
            Assert.True(viewModel.HasPromptWaitingToBeDelivered);
            Assert.Empty(pty.Written);

            _Terminal(view).Write(Encoding.UTF8.GetBytes("\x1b[2J\x1b[H» A changed composer hint"));
            _InvokeCheckReadiness(view);
            _InvokeCheckReadiness(view);
            _InvokeCheckReadiness(view);
            await _LetPostedWorkRunAsync();

            var written = Encoding.UTF8.GetString(pty.Written.ToArray());
            Assert.Equal($"{BracketedPasteStart}review the migration{BracketedPasteEnd}\r", written);
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
        viewModel.SetAutoSubmitScheduler((_, submit) => submit());
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
