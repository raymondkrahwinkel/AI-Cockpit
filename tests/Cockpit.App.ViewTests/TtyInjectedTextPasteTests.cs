using System.Reflection;
using System.Text;
using Avalonia.Controls;
using Avalonia.Threading;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Infrastructure.Sessions;
using Exclr8.Terminal;
using Exclr8.Terminal.Input;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-752/AC-941: <c>TtyView._WriteToPty(string)</c> pastes text bracketed and writes the trailing CR as its own
/// pty write, deferred by AC-64's 60ms beat when the call carries both. These pin that exact byte stream.
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

            _InvokeWriteToPty(view, prompt + "\r");
            await _LetPostedWorkRunAsync();
            var vm = Assert.IsType<TtyViewModel>(view.DataContext);
            vm.UpdateChildAgents([new Cockpit.Core.Sessions.BackgroundTask("child", Cockpit.Core.Sessions.BackgroundTaskKind.SubAgent, "Review")]);
            vm.ChildAgents[0].OpenCommand.Execute(null);
            Assert.True(vm.IsReadingChild);
            await Task.Delay(TtyViewModel.AutoSubmitDelay(prompt.Length) + TimeSpan.FromMilliseconds(100));
            await _LetPostedWorkRunAsync();
            Assert.Equal(3, pty.WriteCalls.Count);

            vm.ReturnToParentCommand.Execute(null);
            _InvokeWriteToPty(view, prompt + "\r");
            await _LetPostedWorkRunAsync();
            FieldInfo? activePty = typeof(TtyView).GetField("_pty", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(activePty);
            activePty.SetValue(view, new FakePty());
            await Task.Delay(TtyViewModel.AutoSubmitDelay(prompt.Length) + TimeSpan.FromMilliseconds(100));
            await _LetPostedWorkRunAsync();
            Assert.Equal(4, pty.WriteCalls.Count);
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

    public enum TtyInputOrigin
    {
        Typed,
        Voice,
        Pushed,
    }

    public static TheoryData<TtyInputOrigin> OperatorInputOrigins => new()
    {
        TtyInputOrigin.Typed,
        TtyInputOrigin.Voice,
        TtyInputOrigin.Pushed,
    };

    [Theory]
    [MemberData(nameof(OperatorInputOrigins))]
    public Task TtyInput_OnlyOperatorTypingAndVoiceWriteOneContentFreeTimelineRow(TtyInputOrigin origin) =>
        HeadlessAvalonia.RunAsync(async () =>
        {
            var (view, pty) = _NewWiredView();
            var vm = (TtyViewModel)view.DataContext!;
            vm.AdoptPaneId("tty-pane");
            var registry = new SessionRegistry();
            registry.Register(new SessionPanelHandle(vm, isEmbedded: false, () => null));
            var directory = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "session-timeline.jsonl");
            var recorder = new SessionTimelineRecorder(path, TimeProvider.System);
            recorder.Start(registry);

            const string Secret = "unique tty input that must stay private 71c4";
            var writeToPty = (Action<string>)typeof(TtyView)
                .GetMethod("_WriteToPty", BindingFlags.NonPublic | BindingFlags.Instance, [typeof(string)])!
                .CreateDelegate(typeof(Action<string>), view);
            vm.PromptSink = writeToPty;

            if (origin == TtyInputOrigin.Typed)
            {
                _InvokeTerminalInput(view, Encoding.UTF8.GetBytes(Secret));
                _InvokeTerminalInput(view, " again"u8.ToArray());
            }
            else if (origin == TtyInputOrigin.Voice)
            {
                vm.InjectVoiceTranscript(Secret);
            }
            else
            {
                Assert.True(await vm.SendPromptAsync(Secret));
            }

            await _WaitForTheDelayedCarriageReturnAsync(pty);
            await recorder.FlushAsync();

            var entries = await new SessionTimelineLog(path, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
                .ReadRecentAsync(int.MaxValue);
            var operators = entries.Where(entry => entry.Kind == SessionTimelineKind.Operator).ToList();
            var isOperator = origin != TtyInputOrigin.Pushed;
            Assert.Equal(isOperator ? 1 : 0, operators.Count);
            if (isOperator)
            {
                Assert.Equal("TtyInput", operators[0].Action?.ToString());
            }

            Assert.DoesNotContain(Secret, await File.ReadAllTextAsync(path));
            recorder.Dispose();
            Directory.Delete(directory, recursive: true);
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

    private static void _InvokeTerminalInput(TtyView view, byte[] bytes) =>
        typeof(TerminalControl)
            .GetMethod("RaiseInput", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(_Terminal(view), [bytes, InputLineOrigin.Typed]);

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
