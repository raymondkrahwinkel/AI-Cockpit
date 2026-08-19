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
/// AC-752: <c>TtyView._WriteToPty(string)</c> now routes injected text through bracketed paste and writes a
/// trailing CR raw, right after. AC-941: that CR now lands in a pty write of its own, deferred by the same 60ms
/// beat AC-64 uses elsewhere - landing in the same pty read as the paste is what let claude's CLI swallow it as a
/// literal newline inside the pasted block instead of Enter, regardless of bracketed paste. These pin the actual
/// byte stream, and the write boundaries, that land in the pty's stdin.
/// </summary>
[Collection("avalonia")]
public class TtyInjectedTextPasteTests
{
    private const string BracketedPasteStart = "[200~";
    private const string BracketedPasteEnd = "[201~";

    [Fact]
    public Task TextWithATrailingCarriageReturn_IsPastedBracketed_ThenTheCarriageReturnLandsRawAfterABeat() =>
        HeadlessAvalonia.RunAsync(async () =>
        {
            var (view, pty) = _NewWiredView();
            var prompt = new string('a', 80); // well over the 62-char/64-byte paste threshold

            // Claude's own CLI turns bracketed paste on (DECSET 2004) as soon as it starts, and `Terminal.Paste`
            // only wraps its output in `ESC[200~`/`ESC[201~` while that mode is active — matching that here so the
            // test exercises the same terminal state the real session is in when this bug bites.
            _Terminal(view).Write("[?2004h"u8.ToArray());

            _InvokeWriteToPty(view, prompt + "\r");
            await _LetPostedWorkRunAsync();

            // AC-941: the paste lands immediately; the CR does not - it is still to come.
            Assert.Single(pty.WriteCalls);

            await _WaitForTheDelayedCarriageReturnAsync(pty);

            Assert.Equal(2, pty.WriteCalls.Count);
            Assert.Equal("\r"u8.ToArray(), pty.WriteCalls[1]);
            var written = Encoding.UTF8.GetString(pty.Written);
            Assert.Equal($"{BracketedPasteStart}{prompt}{BracketedPasteEnd}\r", written);
        });

    [Fact]
    public Task TextWithATrailingCarriageReturn_WithoutBracketedPaste_StillLandsTheCarriageReturnAsASeparatePtyWrite() =>
        HeadlessAvalonia.RunAsync(async () =>
        {
            // AC-941: deliberately no "[?2004h" - BracketedPaste stays false, the exact condition under which the
            // CR used to land in the same pty read as the raw text and get folded into it as a literal newline
            // instead of registering as Enter (measured: a 0ms gap between text and CR reproduces it, 10ms avoids
            // it, regardless of bracketed paste).
            var (view, pty) = _NewWiredView();
            var prompt = new string('a', 80);

            _InvokeWriteToPty(view, prompt + "\r");
            await _LetPostedWorkRunAsync();

            Assert.Single(pty.WriteCalls);
            Assert.DoesNotContain((byte)'\r', pty.WriteCalls[0]);

            await _WaitForTheDelayedCarriageReturnAsync(pty);

            Assert.Equal(2, pty.WriteCalls.Count);
            Assert.Equal("\r"u8.ToArray(), pty.WriteCalls[1]);
            var written = Encoding.UTF8.GetString(pty.Written);
            Assert.Equal($"{prompt}\r", written);
        });

    [Fact]
    public Task ACarriageReturnAlone_WritesARawEnter_NeverWrappedInAPasteAndNeverDelayed() =>
        HeadlessAvalonia.RunAsync(async () =>
        {
            var (view, pty) = _NewWiredView();

            _InvokeWriteToPty(view, "\r");
            await _LetPostedWorkRunAsync();

            // AC-941 criterion 3: a call that is only "\r" (a key press, or route 1's own already-delayed submit)
            // is not additionally delayed - it lands in the very first pty write, with no beat to wait out.
            Assert.Equal("\r"u8.ToArray(), pty.Written);
            Assert.Single(pty.WriteCalls);
        });

    [Fact]
    public Task SendPromptAsync_TheRouteNotifyAndScheduledResumeUse_StillLandsTheCarriageReturnAsASeparatePtyWrite() =>
        HeadlessAvalonia.RunAsync(async () =>
        {
            // AC-941 criterion 5: cockpit-agents notify (WorkspaceAgentGateway.SendPromptAsync) and the AC-234/
            // AC-410 resume paths all reach the pty through TtyViewModel.SendPromptAsync -> PromptSink, which the
            // real view wires straight to this same _WriteToPty(string) (see TtyView.axaml.cs's launch code).
            // Unlike TtyInjectedTextTests's SendPromptAsync test, this wires the real method rather than a mock
            // sink, so it is the actual method the bug lived in.
            var (view, pty) = _NewWiredView();
            var vm = (TtyViewModel)view.DataContext!;
            var writeToPty = (Action<string>)typeof(TtyView)
                .GetMethod("_WriteToPty", BindingFlags.NonPublic | BindingFlags.Instance, [typeof(string)])!
                .CreateDelegate(typeof(Action<string>), view);
            vm.PromptSink = writeToPty;

            var notice = new string('n', 91); // matches the audit-trail's 91-character rendered notice (AC-941)

            Assert.True(await vm.SendPromptAsync(notice));
            await _LetPostedWorkRunAsync();

            Assert.Single(pty.WriteCalls);
            Assert.DoesNotContain((byte)'\r', pty.WriteCalls[0]);

            await _WaitForTheDelayedCarriageReturnAsync(pty);

            Assert.Equal(2, pty.WriteCalls.Count);
            Assert.Equal("\r"u8.ToArray(), pty.WriteCalls[1]);
            var written = Encoding.UTF8.GetString(pty.Written);
            Assert.Equal($"{notice}\r", written);
        });

    // Lets the pty write posted onto the UI thread by `_WriteToPty` actually run: the headless platform's
    // dispatcher loop is real (see `HeadlessAvalonia`), so a job queued after it — same priority — only
    // completes once everything queued ahead of it has.
    private static async Task _LetPostedWorkRunAsync() => await Dispatcher.UIThread.InvokeAsync(() => { });

    // The CR is deferred behind a real 60ms DispatcherTimer (AC-941/AC-64's beat), so - like the resize-settle
    // timer elsewhere in this suite (see TerminalSettle) - waiting it out means real wall-clock time, not just
    // another dispatcher tick.
    private static async Task _WaitForTheDelayedCarriageReturnAsync(FakePty pty)
    {
        for (var poll = 0; poll < 20 && pty.WriteCalls.Count < 2; poll++)
        {
            await Task.Delay(20);
            await Dispatcher.UIThread.InvokeAsync(() => { });
        }
    }

    private static (TtyView View, FakePty Pty) _NewWiredView()
    {
        var view = new TtyView();
        var window = new Window { Content = view, Width = 800, Height = 400 };
        window.Show();
        window.UpdateLayout();

        // Triggers OnDataContextChanged -> WireTerminal(), the same as the real view getting a panel's view model.
        view.DataContext = new TtyViewModel();

        var pty = new FakePty();
        typeof(TtyView).GetField("_pty", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(view, pty);

        return (view, pty);
    }

    private static void _InvokeWriteToPty(TtyView view, string text) =>
        typeof(TtyView)
            .GetMethod("_WriteToPty", BindingFlags.NonPublic | BindingFlags.Instance, [typeof(string)])!
            .Invoke(view, [text]);

    // The x:Name-generated field for the XAML-declared TerminalControl, not exposed publicly by TtyView.
    private static TerminalControl _Terminal(TtyView view) =>
        (TerminalControl)typeof(TtyView).GetField("Terminal", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(view)!;

    private sealed class FakePty : IConPtyProcess
    {
        private readonly _RecordingStream _input = new();

        public Stream InputStream => _input;

        public Stream OutputStream { get; } = new MemoryStream();

        public int ProcessId => 0;

        public byte[] Written => _input.Written;

        // Each entry is one pty write call (_WriteToPty(pty, bytes) does exactly one Write per invocation) - the
        // AC-941 assertions care about how many separate pty reads the text/CR land in, not just the final bytes.
        public IReadOnlyList<byte[]> WriteCalls => _input.WriteCalls;

        public void Resize(short columns, short rows)
        {
        }

        public void Dispose() => _input.Dispose();

        private sealed class _RecordingStream : MemoryStream
        {
            private readonly List<byte[]> _calls = [];

            public IReadOnlyList<byte[]> WriteCalls => _calls;

            public byte[] Written => ToArray();

            // _WriteToPty(pty, bytes) always calls this span overload directly (its parameter is already a
            // ReadOnlySpan<byte>) - that is the one write path this test double needs to record.
            public override void Write(ReadOnlySpan<byte> buffer)
            {
                _calls.Add(buffer.ToArray());
                base.Write(buffer);
            }
        }
    }
}
