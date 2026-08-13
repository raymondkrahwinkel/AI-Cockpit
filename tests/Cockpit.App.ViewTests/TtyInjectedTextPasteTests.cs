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
/// AC-752: claude's CLI (2.1.228) treats any stdin chunk of &gt;=64 bytes as a paste, and a <c>\r</c> riding inside
/// such a chunk becomes a literal newline in the pasted text instead of registering as Enter — so a prompt/voice
/// transcript/scheduled resume over 62 characters, written as text+CR in one go, silently never submits.
/// <c>TtyView._WriteToPty(string)</c> is the shared entry for all three; it now routes the text through the
/// terminal's own bracketed paste (<see cref="Exclr8.Terminal.TerminalControl.Paste"/>, the same route
/// <c>_OnPasteTextAsync</c> uses for an operator's paste) and writes a trailing CR raw, right after — the same
/// sequence <c>_OnPasteTextAsync</c> already relied on being safe. These pin the actual byte stream that lands in
/// the pty's stdin.
/// </summary>
[Collection("avalonia")]
public class TtyInjectedTextPasteTests
{
    private const string BracketedPasteStart = "[200~";
    private const string BracketedPasteEnd = "[201~";

    [Fact]
    public Task TextWithATrailingCarriageReturn_IsPastedBracketed_ThenTheCarriageReturnLandsRaw() =>
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

            var written = Encoding.UTF8.GetString(pty.Written.ToArray());
            Assert.Equal($"{BracketedPasteStart}{prompt}{BracketedPasteEnd}\r", written);
        });

    [Fact]
    public Task ACarriageReturnAlone_WritesARawEnter_NeverWrappedInAPaste() =>
        HeadlessAvalonia.RunAsync(async () =>
        {
            var (view, pty) = _NewWiredView();

            _InvokeWriteToPty(view, "\r");
            await _LetPostedWorkRunAsync();

            Assert.Equal("\r"u8.ToArray(), pty.Written.ToArray());
        });

    // Lets the pty write posted onto the UI thread by `_WriteToPty` actually run: the headless platform's
    // dispatcher loop is real (see `HeadlessAvalonia`), so a job queued after it — same priority — only
    // completes once everything queued ahead of it has.
    private static async Task _LetPostedWorkRunAsync() => await Dispatcher.UIThread.InvokeAsync(() => { });

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
