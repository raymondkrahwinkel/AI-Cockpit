using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Abstractions.Toasts;
using Cockpit.Core.Toasts;
using Cockpit.Infrastructure.Sessions.Tty;
using Exclr8.Terminal;
using Exclr8.Terminal.Buffer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cockpit.App.Views;

// Hosts the real interactive `claude` TUI: an Exclr8 `TerminalControl` (a pure
// byte-in/byte-out terminal-emulator renderer, no PTY plumbing of its own) bridged to a pty (ConPTY
// on Windows, Porta.Pty on Linux/macOS via `IPtyHostFactory`). The code-behind owns the plumbing
// between the two — pty output is written into the control, the control's `Input`/`Output`
// byte events are written to pty stdin, and the control's `Resized` event is relayed to the pty
// — because that bridge is inherently view/toolkit-bound, not view-model logic.
public partial class TtyView : UserControl
{
    private IConPtyProcess? _pty;
    private CancellationTokenSource? _outputCancellation;
    private TtyViewModel? _viewModel;
    private TtyLaunchRequest? _pendingLaunch;
    private bool _launchPending;
    private bool _wired;

    // Whether the last pointer press was a Ctrl+click we opened a link for, so its release is ours to swallow too (AC-560).
    private bool _linkPressConsumed;
    private int _lastColumns;
    private int _lastRows;
    // The size actually last sent to the pty (initial launch or a settle-tick Resize) — #58's reference
    // point for telling a real resize apart from a net-zero round trip. See TtyResizeSettleDecision.
    private int _ptyColumns;
    private int _ptyRows;
    // Coalesces the terminal's resize burst so the pty is spawned/resized once the size settles, not on every
    // intermediate value. On Wayland/KDE the compositor emits a transient size before the real one; spawning
    // claude on the transient size and immediately reflowing it is a prime cause of the stacked-at-top render.
    private DispatcherTimer? _resizeSettle;

    // AC-57: cap how often streaming pty output repaints the terminal. Exclr8's TerminalControl re-shapes all
    // visible text (HarfBuzz shaping + glyph-run construction, no cross-frame cache) on every Render, so one
    // repaint per pty chunk during a burst is a per-frame allocation storm — ~16 MB/s here, ~88 MB/s on macOS,
    // where Metal retains it as native memory until the app falls over (the runaway this issue is about). The
    // pty reader now appends into _outputPending on its background thread and a UI-thread timer flushes it into
    // Terminal.Write at ~30 fps, so the control invalidates (and re-shapes) at a bounded rate rather than on
    // every chunk. All bytes still arrive, in order; only the paint cadence is capped, and 30 fps stays smooth.
    private const int OutputFlushIntervalMs = 33;
    private readonly object _outputLock = new();
    private readonly List<byte> _outputPending = [];
    private DispatcherTimer? _outputFlush;

    // AC-760: a held brief may reach the pty only once the CLI is actually reading stdin, not merely once the
    // process exists — readiness rides the DECSET 2004 flag through the same flush that drains every pty byte.
    // ponytail: 15s fallback for a CLI that never enables bracketed paste, fixed rather than a setting.
    private static readonly TimeSpan HostedTuiReadyFallback = TimeSpan.FromSeconds(15);
    private DateTime? _firstPtyOutputAtUtc;
    private bool _hostedTuiReady;

    // #58 confirmation logging: every Exclr8 Resized event and every pty.Resize call, so the net-zero
    // round-trip signature (>=2 Resized with different sizes within the settle window, followed by one
    // pty.Resize equal to the previous pty size) can be confirmed from %APPDATA%\Cockpit\logs\cockpit.log.
    // Resolved from the app's DI container rather than injected: this UserControl is constructed by the
    // XAML view locator/designer, not by the container, matching the existing Program.Services lookups in
    // App.axaml.cs. Absent wherever no container was built — the XAML previewer, and the headless harness the view
    // tests render scenes in, which is the same condition and used to be checked as if it were only the first one.
    private readonly ILogger<TtyView>? _logger =
        Program.Services?.GetService<ILogger<TtyView>>();

    // AC-2 user feedback: a toast when claude's clipboard write (OSC 52) actually reaches the OS clipboard and
    // when a clicked link is handed to the browser, so the action is visibly acknowledged. Resolved from the app
    // container the same way as _logger (this control is built by the view locator, not the DI graph).
    private readonly IToastService? _toast =
        Program.Services?.GetService<IToastService>();

    // AC-34: the terminal-access registry this pane feeds when an agent is coupled to it — the pane registers on
    // launch, unregisters on close, and hands its rendered output to the registry (only while coupled, so read-scope
    // starts at the coupling). Resolved from the app container like the logger/toast, since this control is built by
    // the view locator, not the DI graph.
    private readonly ITerminalAccessRegistry? _terminals =
        Program.Services?.GetService<ITerminalAccessRegistry>();

    // AC-34: the operator types on the UI thread while a coupled agent's send_terminal arrives on an MCP request
    // thread, both into this pane's single pty stdin. A Stream is not thread-safe, so two writes can interleave into
    // a garbled command line — which is the one thing a shared pane must not do. Every write into the pty goes
    // through _WriteToPty and takes this lock; the writes are a handful of bytes, so nothing waits for long.
    private readonly object _ptyWriteLock = new();

    // Which usage signals this session's provider declares, and how it reads its own statusline snapshot (AC-229).
    private readonly IPluginTtyProviderRegistry? _ttyProviders =
        Program.Services?.GetService<IPluginTtyProviderRegistry>();

    public TtyView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;

        // Push-to-talk (F9 by default): tunnel so we intercept it before the Terminal control's own
        // KeyDown handling would otherwise encode it as a VT keystroke and send it into the pty.
        AddHandler(InputElement.KeyDownEvent, _OnPushToTalkKeyDown, RoutingStrategies.Tunnel);
        AddHandler(InputElement.KeyUpEvent, _OnPushToTalkKeyUp, RoutingStrategies.Tunnel);

        // A newline inside the prompt, the way every chat does it: Shift+Enter or Alt+Enter. Tunnel, because the
        // terminal control encodes Enter as a bare carriage return whatever else is held down — so the agent saw
        // "send it" and the line break was lost.
        AddHandler(InputElement.KeyDownEvent, _OnNewlineKeyDown, RoutingStrategies.Tunnel);

        // Scrollback dispatch for the terminal's mouse wheel (#56 alt-screen arrow-key fallback, #57
        // primary/inline-screen native scroll): tunnel so we intercept before TerminalControl's own
        // OnPointerWheelChanged would otherwise run unconditionally — see OnTerminalWheel/TtyWheelScrollGate.
        AddHandler(InputElement.PointerWheelChangedEvent, OnTerminalWheel, RoutingStrategies.Tunnel);

        // AC-2: Ctrl+click to follow a link. The terminal control forwards every click to the pty (and skips its
        // own link activation) whenever the running app has mouse reporting on — claude's TUI does — and offers no
        // modifier bypass, so a plain or Ctrl click never opened a URL. Tunnel so we hit-test the link and open it
        // before TerminalControl's own OnPointerPressed runs; Ctrl (not a plain click) so text selection by drag
        // is untouched. Handled only when a link is actually under the pointer, so every other click still reaches
        // claude. Not Windows-gated: the same Exclr8 control renders on every OS, so the gap and the fix are shared.
        AddHandler(InputElement.PointerPressedEvent, OnTerminalPointerPressedForLinks, RoutingStrategies.Tunnel);

        // AC-560: and its release. The control reports a release to the pty on mouse-reporting mode without checking
        // it saw the press, so swallowing only the press left the TUI a lone release over the link — which claude
        // opens itself, giving two browser tabs for one click.
        AddHandler(InputElement.PointerReleasedEvent, OnTerminalPointerReleasedForLinks, RoutingStrategies.Tunnel);

        // AC-34: reflect this pane's coupling on the "agent connected" bar, so it is always visible when an agent is
        // on the pane (the counterpart to both sides being able to type). Unsubscribed on unload.
        if (_terminals is { } terminals)
        {
            terminals.CouplingChanged += OnCouplingChanged;
        }
    }

    // Writes keystrokes into the pty, serialised against every other writer (see `_ptyWriteLock`).
    // Returns false when the write did not land — the pty may have exited between the keystroke and here, which the
    // output pump observes and reports; losing a keystroke to a dead shell is not worth taking the cockpit down for.
    private bool _WriteToPty(IConPtyProcess pty, ReadOnlySpan<byte> bytes)
    {
        try
        {
            lock (_ptyWriteLock)
            {
                pty.InputStream.Write(bytes);
                pty.InputStream.Flush();
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // AC-34: a coupling changed on some pane. If it is ours, show or hide the agent bar — on the UI thread, since the
    // event fires from an MCP request thread (couple) or a teardown (decouple).
    private void OnCouplingChanged(TerminalCouplingChange change)
    {
        if (_viewModel?.PaneId is not { } paneId || !string.Equals(change.PaneId, paneId, StringComparison.Ordinal))
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_viewModel is null)
            {
                return;
            }

            // The bar says which of the two the operator granted: an agent that may only read is not "connected" in
            // the sense the Disconnect tooltip means, and calling it that would overstate what they agreed to.
            _viewModel.AgentConnected = change.Coupling is not null;
            _viewModel.AgentConnectedLabel = change.Coupling switch
            {
                TerminalCouplingMode.Drive => $"Agent connected — {_ResolveAgentSessionName(change.AgentSession)}",
                TerminalCouplingMode.Watch => $"Agent reading — {_ResolveAgentSessionName(change.AgentSession)}",
                _ => null,
            };
            _viewModel.AgentCanType = change.Coupling == TerminalCouplingMode.Drive;
        });
    }

    // AC-34: the bar shows the coupled agent session's operator-facing name (e.g. "work-6"), not the raw pane-id guid
    // the coupling is keyed on. Resolved from the cockpit's live session list the way the plugin hosts reach it (this
    // control is built by the view locator, not the DI graph); the guid stays as the fallback when no session matches
    // it — e.g. the driving session has already closed.
    private static string? _ResolveAgentSessionName(string? agentSession)
    {
        if (string.IsNullOrEmpty(agentSession))
        {
            return agentSession;
        }

        var name = Program.Services?.GetService<CockpitViewModel>()?
            .Sessions.FirstOrDefault(session => string.Equals(session.PaneId, agentSession, StringComparison.Ordinal))?
            .Title;

        return string.IsNullOrWhiteSpace(name) ? agentSession : name;
    }

    // AC-34: the operator's Disconnect on the agent bar — the reactive kill-switch. The registry interrupts a running
    // command and breaks the coupling, and its CouplingChanged event hides the bar again.
    private void OnAgentDisconnect(object? sender, RoutedEventArgs e)
    {
        if (_viewModel?.PaneId is { Length: > 0 } paneId)
        {
            _terminals?.Disconnect(paneId);
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.LaunchRequested -= OnLaunchRequested;
            _viewModel.VoiceTranscriptReady -= _OnVoiceTranscriptReady;
            _viewModel.PasteTextAsync = null;
            // And the prompt route, which points into this view's pty just as the paste delegate does: a view model
            // handed to another view must not go on answering "yes, a prompt reaches me" through the one it left.
            _viewModel.PromptSink = null;
            _viewModel.PropertyChanged -= _OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as TtyViewModel;
        if (_viewModel is not null)
        {
            _viewModel.LaunchRequested += OnLaunchRequested;
            _viewModel.VoiceTranscriptReady += _OnVoiceTranscriptReady;
            _viewModel.PasteTextAsync = _OnPasteTextAsync;
            _viewModel.PropertyChanged += _OnViewModelPropertyChanged;
            // The profile may already have been configured (dialog confirmed) before this view existed;
            // pull any pending launch now that we are subscribed. The VM's guard makes this fire once.
            _viewModel.TryRaiseLaunch();
        }

        WireTerminal();
        _ApplyTerminalFont();
    }

    // TerminalControl.FontFamily/FontSize are plain CLR properties, not registered AvaloniaProperties,
    // so they can't be targeted by a compiled XAML binding (#40) — applied here imperatively instead,
    // both on attach and every time the global terminal-settings VM property changes, so Options →
    // Terminal takes effect live. Both setters re-measure the cell and reflow the grid on assignment,
    // which raises `Exclr8.Terminal.TerminalControl.Resized` if the new metrics change the
    // column/row count — `OnTerminalResized` then resizes the pty to match, the same as a
    // window resize.
    private void _OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TtyViewModel.TerminalFontFamily) or nameof(TtyViewModel.TerminalFontSize))
        {
            _ApplyTerminalFont();
        }
    }

    private void _ApplyTerminalFont()
    {
        if (_viewModel is null)
        {
            return;
        }

        Terminal.FontFamily = _viewModel.TerminalFontFamily;
        Terminal.FontSize = _viewModel.TerminalFontSize;
    }


    // KeyDown for the push-to-talk hotkey — see the equivalent handler on `SessionView` for
    // the guard reasoning. No-ops when global push-to-talk is active (see
    // `PushToTalkKeyGate`) so the global coordinator's hold does not fire twice.
    private void _OnPushToTalkKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is { } vm
            && PushToTalkKeyGate.ShouldHandleLocally(e.Key, vm.PushToTalkKeyName, vm.GlobalPushToTalkEnabled)
            && vm.BeginVoiceHold())
        {
            _holdingKey = e.Key;
            e.Handled = true;
        }
    }

    // The key whose press opened a microphone here, until its own release ends the hold — see `_OnPushToTalkKeyUp`.
    private Key? _holdingKey;

    // KeyUp for the push-to-talk hotkey: ends the hold and transcribes — see `TtyViewModel.OnVoiceTextReady`.
    // Ends only what this view's own KeyDown started (AC-557): a release also arrives for a press that started
    // nothing — voice off for this session — and ending a hold that never began throws. Which is also why this
    // asks the key that started it rather than the gate again: the gate's answer can change mid-hold, and a hold
    // nobody ends holds the microphone for good.
    private void _OnPushToTalkKeyUp(object? sender, KeyEventArgs e)
    {
        if (_holdingKey == e.Key && _viewModel is { } vm)
        {
            _holdingKey = null;
            e.Handled = true;
            _ = vm.EndVoiceHoldAsync();
        }
    }


    // Pastes text into the terminal on the session's behalf (AC-341) — the path of a screenshot the operator
    // just took, handed to the control the same way any other paste is.
    // `Paste` rather than a synthesised Ctrl+V, which is what this did while the image travelled by
    // clipboard: the key made the control go and read the clipboard, and the control's own paste is what this
    // wanted from it in the first place. Nothing has to negotiate an image format now, because nothing but text
    // crosses this line.
    //
    // The terminal is focused first, not because the paste needs it — it writes to the pty directly — but because
    // the operator's next move is to type the sentence that goes with the screenshot.
    private Task _OnPasteTextAsync(string text)
    {
        var pasted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                Terminal.Focus();
                Terminal.Paste(text);
            }
            finally
            {
                pasted.TrySetResult();
            }
        });

        return pasted.Task;
    }

    // Writes a finished voice transcript into the pty — the same path a scheduled resume takes (`_WriteToPty(string)`).
    private void _OnVoiceTranscriptReady(string text) => _WriteToPty(text);

    // AC-752: claude's CLI treats any stdin chunk >=64 bytes as a paste, swallowing a `\r` inside it as a literal
    // newline instead of Enter. Route the text through bracketed paste (as `_OnPasteTextAsync` already does) and
    // write a trailing CR raw right after, on the UI thread so the two land in order.
    private void _WriteToPty(string text)
    {
        var pty = _pty;
        if (pty is null)
        {
            return;
        }

        var hasTrailingReturn = text.Length > 0 && text[^1] == '\r';
        var typed = hasTrailingReturn ? text[..^1] : text;

        Dispatcher.UIThread.Post(() =>
        {
            if (typed.Length > 0)
            {
                Terminal.Paste(typed);
            }

            if (hasTrailingReturn)
            {
                _WriteToPty(pty, [(byte)'\r']);
            }
        });
    }

    private void WireTerminal()
    {
        if (_wired)
        {
            return;
        }

        _wired = true;
        // Both events carry bytes the terminal wants written back to the pty: Input is the user's
        // keystrokes/paste, Output is protocol replies (DSR/DA/DECRQM/OSC-query) the terminal itself
        // generates. Both go to the same place.
        Terminal.Input += OnTerminalBytesToPty;
        Terminal.Output += OnTerminalBytesToPty;
        Terminal.Resized += OnTerminalResized;

        // AC-2 (Windows especially): honour claude's clipboard writes and make URLs clickable. Both are opt-in
        // terminal-emulator features. claude copies via OSC 52; on Linux it also reaches the clipboard through
        // native tools (xclip/wl-copy) so it worked there regardless, but on Windows it relies on the terminal —
        // and AllowClipboardAccess defaults off (it lets a remote process scrape the clipboard), so Cockpit
        // silently dropped the write and "copied" never landed. This is the operator's own local claude session,
        // so we opt in. Links are auto-detected by WebLinkProvider (http/https spans) and, once clicked, opened
        // in the OS browser; LinkActivationPolicy is the safety gate that keeps anything but http/https out.
        Terminal.AllowClipboardAccess = true;
        Terminal.ClipboardRequested += OnClipboardRequested;
        // Register the URL matcher so links are detected and underlined; opening them is entirely ours, through the
        // Ctrl+click handler above (OnTerminalPointerPressedForLinks). We deliberately do NOT subscribe the control's
        // own HyperlinkClicked: it fires on a plain click whenever mouse reporting happens to be off, which — on top
        // of our Ctrl+click — opened every link twice. One opener, one gesture (Ctrl+click), on every mouse mode.
        Terminal.RegisterLinkProvider(new WebLinkProvider());
    }

    // claude asked to write to the OS clipboard (OSC 52). Honour it against the real system clipboard and
    // acknowledge it with a toast, so the copy the TUI reports is one the operator can see actually happened.
    private void OnClipboardRequested(object? sender, ClipboardRequestEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        _ = _SetClipboardAsync(clipboard, e.Text);
    }

    private async Task _SetClipboardAsync(IClipboard clipboard, string text)
    {
        try
        {
            await clipboard.SetTextAsync(text);
            _toast?.Show("Copied to clipboard", ToastSeverity.Success);
        }
        catch (Exception ex)
        {
            // A clipboard the OS momentarily locked must not take down the TUI; surface it quietly instead.
            _logger?.LogDebug(ex, "TTY clipboard write (OSC 52) failed");
            _toast?.Show("Could not access the clipboard", ToastSeverity.Warning);
        }
    }

    // Ctrl+click over a link when the terminal cannot activate it itself (mouse reporting is on, so the click is
    // otherwise forwarded to claude). Hit-tests the cell under the pointer for an OSC 8 or provider-detected URL
    // and opens it, consuming the click so it never reaches the pty.
    private void OnTerminalPointerPressedForLinks(object? sender, PointerPressedEventArgs e)
    {
        _linkPressConsumed = false;
        var pointer = e.GetCurrentPoint(Terminal);
        if (!TerminalLinkGesture.Opens(
                e.KeyModifiers.HasFlag(KeyModifiers.Control), pointer.Properties.IsLeftButtonPressed, e.ClickCount))
        {
            return;
        }

        if (_LinkAt(pointer.Position) is not { } url)
        {
            return;
        }

        if (_TryOpenLink(url))
        {
            _linkPressConsumed = true;
            e.Handled = true;
        }
    }

    // Swallows the release belonging to a Ctrl+click we already opened a link for (AC-560). TerminalControl reports
    // a release to the pty whenever mouse reporting is on without checking it saw the press, so the TUI received a
    // lone release over the link and opened it a second time.
    private void OnTerminalPointerReleasedForLinks(object? sender, PointerReleasedEventArgs e)
    {
        if (!_linkPressConsumed)
        {
            return;
        }

        _linkPressConsumed = false;
        e.Handled = true;
    }

    // The URL clickable at `position` (pointer coordinates relative to the terminal), or null.
    // Mirrors TerminalControl's own hit-test (OSC 8 cell first, then registered plain-URL providers) using its
    // public buffer API; only the pixel-to-cell mapping (`GridPos`) is not public, so it is reached by
    // reflection and any miss degrades to "no link", never a throw.
    private string? _LinkAt(Point position)
    {
        try
        {
            if (_GridPosMethod?.Invoke(Terminal, [position]) is not ITuple cell || cell[0] is not int row || cell[1] is not int col)
            {
                return null;
            }

            var cells = Terminal.Buffer.GetRowForRender(row);
            if (cells is null || col < 0 || col >= cells.Length)
            {
                return null;
            }

            if (cells[col].HyperlinkId != 0 && Terminal.Buffer.TryGetHyperlink(cells[col].HyperlinkId, out var oscUrl) && !string.IsNullOrEmpty(oscUrl))
            {
                return oscUrl;
            }

            var rowText = RowText.Build(cells, out var columnMap);
            foreach (var provider in Terminal.LinkProviders)
            {
                foreach (var link in provider.Provide(rowText))
                {
                    var start = columnMap[link.StartCol];
                    var end = columnMap[Math.Min(link.EndCol - 1, columnMap.Length - 1)];
                    if (col >= start && col <= end)
                    {
                        return link.Url;
                    }
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "TTY link hit-test failed");
            return null;
        }
    }

    // Opens an http/https URL in the OS browser with a toast; returns whether it was handled (a browsable URL).
    //
    // Parses first and opens the address it got back, rather than asking `ExternalLink.TryOpen(string)`
    // straight away: this is the one caller that has to tell "not a link at all" — which the terminal must leave to
    // whatever else wants the click — apart from "a link the browser would not open", which it reports.
    private bool _TryOpenLink(string url)
    {
        if (!ExternalLink.TryParseWebAddress(url, out var address))
        {
            return false;
        }

        if (ExternalLink.TryOpen(address))
        {
            _toast?.Show($"Opening {address.Host} in your browser", ToastSeverity.Information);
        }
        else
        {
            // The shared opener swallows the exception, so what is left to log is which URL failed — enough to tell a
            // missing browser on this machine from a link the terminal mis-detected.
            _logger?.LogDebug("TTY hyperlink launch failed for {Url}", address.AbsoluteUri);
            _toast?.Show("Could not open the link", ToastSeverity.Warning);
        }

        return true;
    }

    // AC-2 link hit-test: pixel→cell is TerminalControl.GridPos, which is not public. Cached once; a null here (an
    // Exclr8 version that renamed it) simply means Ctrl+click stops opening links, never a crash.
    private static readonly System.Reflection.MethodInfo? _GridPosMethod = typeof(TerminalControl)
        .GetMethod("GridPos", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    // A profile and its start defaults have been resolved. The pty can only be spawned once the
    // terminal has a real size, so remember the request and launch on the next
    // `OnTerminalResized` (or now if a size is already known).
    private void OnLaunchRequested(TtyLaunchRequest request)
    {
        _pendingLaunch = request;
        _launchPending = true;

        if (_lastColumns > 0 && _lastRows > 0)
        {
            StartPty();
        }
    }

    private void OnTerminalResized(object? sender, (int Cols, int Rows) e)
    {
        _lastColumns = Math.Max(1, e.Cols);
        _lastRows = Math.Max(1, e.Rows);
        UpdateDiagnostics();

        // #58 confirmation logging: the glitch's signature is >=2 of these with different sizes within
        // the ~150ms settle window, followed by exactly one "pty.Resize" log line equal to the previous
        // pty size (see CreateResizeSettleTimer below). RenderScaling is logged alongside so a fractional-
        // scaling renegotiation showing up as the trigger is visible directly in the log.
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        _logger?.LogInformation(
            "Exclr8 Resized -> {Columns}x{Rows} (scale {Scale})", _lastColumns, _lastRows, scale);

        // Debounce: (re)start the settle timer and act only once the size stops changing (see _resizeSettle).
        _resizeSettle ??= CreateResizeSettleTimer();
        _resizeSettle.Stop();
        _resizeSettle.Start();
    }

    private DispatcherTimer CreateResizeSettleTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_launchPending)
            {
                StartPty();
                return;
            }

            if (_pty is not { } pty)
            {
                return;
            }

            // #58: decide deterministically instead of unconditionally resizing. A settled size that
            // differs from what the pty already has is a real resize — claude sees a changed winsize and
            // repaints via SIGWINCH on its own. A settled size that nets back to the pty's current size is
            // the net-zero round trip (Exclr8's buffer mutated getting there, but claude's winsize never
            // changed, so it never got a SIGWINCH to repaint from) — force the redraw claude otherwise
            // never triggers instead of sending an identical, no-op resize.
            var decision = TtyResizeSettleDecision.Decide(_ptyColumns, _ptyRows, _lastColumns, _lastRows);
            if (decision == TtyResizeSettleAction.Resize)
            {
                _logger?.LogInformation(
                    "pty.Resize -> {Columns}x{Rows} (was {PreviousColumns}x{PreviousRows})",
                    _lastColumns, _lastRows, _ptyColumns, _ptyRows);
                pty.Resize((short)_lastColumns, (short)_lastRows);
                _ptyColumns = _lastColumns;
                _ptyRows = _lastRows;
            }
            else
            {
                _logger?.LogInformation(
                    "Net-zero resize round trip at {Columns}x{Rows} -> ForceRedraw", _lastColumns, _lastRows);
                ForceRedraw();
            }
        };

        return timer;
    }

    // Forces the TUI to repaint: shrinks the pty a couple of rows, waits for claude to react to the resize
    // (SIGWINCH), then restores the real size so claude re-renders its managed UI. Recovers the reflow
    // glitch where claude's frames end up stacked at the top after a resize/focus change on some setups —
    // fired deterministically by the resize-settle timer's net-zero-round-trip decision (#58,
    // `TtyResizeSettleDecision`), by the #55 auto-redraw vangnet for a pure focus/activation
    // event with no resize transient at all, and manually via the Redraw button. Does not clear the
    // emulator (that would wipe the scrolled-back conversation claude never re-emits).
    private async void ForceRedraw()
    {
        var pty = _pty;
        if (pty is null || _lastColumns <= 0 || _lastRows <= 0)
        {
            return;
        }

        // #58 diagnostics: snapshot right before and right after the two-step resize below, so a
        // reproduction shows exactly what render state ForceRedraw() started from and whether it actually
        // changed anything.
        _logger?.LogInformation("TTY-DIAG [redraw] before: {Snapshot}", TtyDiagnosticsSnapshot.Capture(Terminal.Buffer));

        try
        {
            // A genuine two-step resize: shrink, let claude react to the SIGWINCH, then restore. No emulator
            // clear — claude only re-emits its sticky UI, not the scrolled-back conversation, so clearing
            // would blank the history.
            pty.Resize((short)_lastColumns, (short)Math.Max(1, _lastRows - 2));
            await Task.Delay(90);
            pty.Resize((short)_lastColumns, (short)_lastRows);
        }
        catch (Exception)
        {
            // The pty may have exited; the output pump already handles that.
        }

        _logger?.LogInformation("TTY-DIAG [redraw] after: {Snapshot}", TtyDiagnosticsSnapshot.Capture(Terminal.Buffer));
    }

    private DispatcherTimer CreateOutputFlushTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(OutputFlushIntervalMs) };
        timer.Tick += (_, _) => _FlushOutput();
        return timer;
    }

    private void OnRedrawClick(object? sender, RoutedEventArgs e) => ForceRedraw();

    private void UpdateDiagnostics()
    {
        if (_viewModel is null)
        {
            return;
        }

        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        var session = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
        var lang = Environment.GetEnvironmentVariable("LANG");
        var lcAll = Environment.GetEnvironmentVariable("LC_ALL");

        var parts = new StringBuilder();
        parts.Append(CultureInfo.InvariantCulture, $"{RuntimeInformation.OSDescription} · grid {_lastColumns}×{_lastRows} · scale {scale.ToString("0.##", CultureInfo.InvariantCulture)}");
        if (!string.IsNullOrEmpty(session))
        {
            parts.Append(CultureInfo.InvariantCulture, $" · {session}");
        }

        parts.Append(CultureInfo.InvariantCulture, $" · LANG={lang ?? "(unset)"} LC_ALL={lcAll ?? "(unset)"}");

        // AC-129: which family the renderer really draws with, and the advance every column position is a
        // multiple of. The configured list leads with Windows-only families, so on another platform the name
        // that wins is whatever the font manager substitutes — and a substitute that is not monospace cannot
        // hold the grid. Without this the operator (and the bug report) can only guess at it.
        parts.Append(CultureInfo.InvariantCulture,
            $" · font {Terminal.EffectiveFontFamily}{(Terminal.UsingMonospaceFallback ? " (substituted)" : "")}" +
            $" · cell {Terminal.CellWidth.ToString("0.###", CultureInfo.InvariantCulture)}px");

        _viewModel.Diagnostics = parts.ToString();

    }

    private void StartPty()
    {
        if (!_launchPending || _pendingLaunch is null || _lastColumns <= 0 || _lastRows <= 0)
        {
            return;
        }

        _launchPending = false;

        try
        {
            // Recommended before connecting a freshly-spawned pty: clears any dimension-detection
            // races from the app's own startup so they don't leave stacked partial renders behind.
            Terminal.PrepareForNewSession();

            // AC-760: this view (and possibly its view model) can be reused for a fresh pty (e.g. AC-564's
            // restart-without-resume), so the readiness gate must reset with it — otherwise a re-launch would
            // inherit "ready" from the session before.
            _firstPtyOutputAtUtc = null;
            _hostedTuiReady = false;
            if (DataContext is TtyViewModel readinessTarget)
            {
                readinessTarget.ResetHostedTuiReadiness();
            }

            var pty = _pendingLaunch.Launcher.Launch(
                _pendingLaunch.Provider,
                _pendingLaunch.Profile,
                _pendingLaunch.Options,
                (short)_lastColumns,
                (short)_lastRows,
                _pendingLaunch.WorkingDirectory,
                _pendingLaunch.Resume,
                // AC-13: the pane id becomes COCKPIT_PANE_ID in the CLI's environment, so the agent can set its own statusline.
                _viewModel?.PaneId,
                // #44: the per-session MCP checklist, so the provider narrows the registry to the operator's selection.
                _pendingLaunch.EnabledMcpServerNames,
                // AC-165: what the plugins gave this session, resolved before the launch was configured.
                _pendingLaunch.Contributed,
                // AC-218: the project this session runs under, so the MCP fan-out resolves against its registry view.
                _pendingLaunch.ProjectId);
            _pty = pty;
            _ptyColumns = _lastColumns;
            _ptyRows = _lastRows;

            // The session's own usage lands in the file its statusline writes; the launched process is what knows
            // which file that is, and the provider that wrote it is what knows how to read it (AC-229).
            if (pty is ITtyStatusFile { StatusFile: { } statusFile } && DataContext is TtyViewModel viewModel)
            {
                var provider = _ttyProviders?.Resolve(_pendingLaunch.Provider.ProviderId);
                viewModel.UsageProviderId = _pendingLaunch.Provider.ProviderId;
                viewModel.TrackLimits(statusFile, provider?.UsageSignals ?? [], provider?.ReadUsage);
            }
            // A scheduled resume (AC-234) arrives the way a keystroke does — the pty's stdin — so the view, which
            // owns the pty, is where that route is handed to the session.
            if (DataContext is TtyViewModel promptTarget)
            {
                promptTarget.PromptSink = _WriteToPty;
            }

            // The pty owns the process, so the view is where the meter (#78) learns which one this session is.
            if (_viewModel is not null)
            {
                _viewModel.ProcessId = pty.ProcessId;
            }
            _logger?.LogInformation("pty launched at {Columns}x{Rows}", _ptyColumns, _ptyRows);

            // AC-34: this pane is now a live terminal an agent could ask to use. Register it under its pane id and the
            // name the operator sees (so list_terminals can name it), plus the input sink that a coupled agent's
            // send_terminal writes through — the same pty stdin the operator's own keystrokes go to. IsTerminal says
            // whether this is a plain shell; a pane running an agent CLI registers too but is never offered to an agent.
            if (_viewModel?.PaneId is { Length: > 0 } paneId && _terminals is { } terminals)
            {
                terminals.PaneOpened(
                    paneId,
                    string.IsNullOrWhiteSpace(_viewModel.Title) ? paneId : _viewModel.Title,
                    _viewModel.IsTerminal);
                terminals.RegisterInput(paneId, bytes => _WriteToPty(pty, bytes.Span));
            }
        }
        catch (Exception ex)
        {
            Terminal.Write(Encoding.UTF8.GetBytes($"\r\nFailed to launch TUI: {ex.Message}\r\n"));
            _viewModel?.OnLaunchFailed();
            return;
        }

        // AC-57: the ~30 fps flush timer that drains the pty reader's buffer into Terminal.Write, capping the
        // repaint (and text re-shape) rate. Created here on the UI thread, alongside the reader it feeds.
        _outputFlush ??= CreateOutputFlushTimer();
        _outputFlush.Start();

        _outputCancellation = new CancellationTokenSource();
        _ = PumpOutputAsync(_pty, _outputCancellation.Token);
        _viewModel?.OnLaunchSucceeded();
    }

    // Shift+Enter and Alt+Enter mean "another line, do not send yet". The pty carries no modifier bits, so what goes
    // down the wire is meta-Enter — ESC then CR — which is what a readline-style prompt (Claude's among them) reads as
    // a line break rather than as submit.
    private void _OnNewlineKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not Key.Enter || (e.KeyModifiers & (KeyModifiers.Shift | KeyModifiers.Alt)) == 0)
        {
            return;
        }

        if (_pty is not { } pty)
        {
            return;
        }

        // ESC then CR (meta-Enter), built from the bytes so no raw escape sits in this source file.
        if (!_WriteToPty(pty, [0x1b, 0x0d]))
        {
            return;
        }

        e.Handled = true;
    }

    private void OnTerminalBytesToPty(object? sender, ReadOnlyMemory<byte> e)
    {
        var pty = _pty;
        if (pty is null)
        {
            return;
        }

        _WriteToPty(pty, e.Span);
    }

    // Terminal-surface mouse wheel: dispatches per `TtyWheelScrollGate` instead of letting
    // `TerminalControl`'s own wheel handling run unconditionally.
    // `TtyWheelScrollAction.ForwardArrowKeys` (#56) — alternate screen, no mouse tracking:
    // Exclr8.Terminal's alternate screen keeps no scrollback, so this sends an Up/Down arrow-key press to
    // the pty instead (mirrors xterm's alternateScroll). `TtyWheelScrollAction.NativeScroll`
    // (#57) — primary/inline screen, which is what Claude Code's TUI actually renders on (capture-
    // confirmed: no alt-screen escape anywhere): scrolls Exclr8's own primary-screen scrollback directly
    // via `TerminalBuffer.ScrollViewUp`/`ScrollViewDown`, which is real and populated (only the
    // alternate screen's `ScrollbackLimit` is zeroed). Both mark the event handled so
    // `TerminalControl`'s own `OnPointerWheelChanged` does not also run.
    // `TtyWheelScrollAction.PassThrough` — alt screen with mouse tracking requested: left
    // alone, `TerminalControl`'s own SGR-mouse-report path already covers it.
    private void OnTerminalWheel(object? sender, PointerWheelEventArgs e)
    {
        var buffer = Terminal.Buffer;
        switch (TtyWheelScrollGate.Decide(buffer.IsAltScreen, buffer.MouseMode))
        {
            case TtyWheelScrollAction.NativeScroll:
                if (e.Delta.Y > 0)
                {
                    buffer.ScrollViewUp(TtyWheelScrollGate.NativeScrollLinesPerNotch);
                }
                else
                {
                    buffer.ScrollViewDown(TtyWheelScrollGate.NativeScrollLinesPerNotch);
                }

                e.Handled = true;
                return;

            case TtyWheelScrollAction.PassThrough:
                return;

            case TtyWheelScrollAction.ForwardArrowKeys:
                var pty = _pty;
                if (pty is null)
                {
                    return;
                }

                _WriteToPty(pty, TtyWheelScrollGate.EncodeArrowKey(e.Delta.Y > 0, buffer.ApplicationCursorKeys));

                e.Handled = true;
                return;
        }
    }

    private async Task PumpOutputAsync(IConPtyProcess pty, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await pty.OutputStream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                // AC-57: hand the bytes to the UI-thread flush timer instead of writing (and repainting) per read.
                // Copied out under the lock before the next ReadAsync overwrites the buffer.
                lock (_outputLock)
                {
                    _outputPending.AddRange(buffer.AsSpan(0, read));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on unload/close.
        }
        catch (Exception)
        {
            // Pipe broken (process exited); fall through to the exit notification.
        }

        // Drain whatever the reader accumulated before announcing the exit, so the last frame of output is not
        // left sitting in the buffer when the process ends between flush ticks.
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _FlushOutput();
            _viewModel?.OnProcessExited(_LastVisibleLines());
        });
    }

    // The last few non-blank lines on screen, newest last — what a restored pane's degraded offer shows as why
    // (AC-410) when the process exits before doing anything, e.g. `claude --resume` printing "No conversation
    // found" and quitting. Read straight off the render buffer rather than kept separately: `_FlushOutput`
    // above already wrote everything pending into it before this runs, so it holds exactly what the operator would
    // have seen.
    private string? _LastVisibleLines(int maxLines = 6)
    {
        var buffer = Terminal.Buffer;
        var lines = new List<string>();

        for (var row = buffer.Rows - 1; row >= 0 && lines.Count < maxLines; row--)
        {
            if (buffer.GetRowForRender(row) is not { } cells)
            {
                continue;
            }

            var text = RowText.Build(cells, out _).TrimEnd();
            if (text.Length > 0)
            {
                lines.Add(text);
            }
        }

        if (lines.Count == 0)
        {
            return null;
        }

        lines.Reverse();
        return string.Join('\n', lines);
    }

    // Writes everything the pty reader has accumulated in one Terminal.Write, on the UI thread. Driven by the
    // ~30 fps flush timer (and once more on exit) so the terminal repaints at a bounded rate under a burst — see
    // the _outputFlush field comment. A no-op when nothing is pending, so an idle session costs nothing.
    private void _FlushOutput()
    {
        byte[]? chunk = null;
        lock (_outputLock)
        {
            if (_outputPending.Count > 0)
            {
                chunk = [.. _outputPending];
                _outputPending.Clear();
            }
        }

        if (chunk is { Length: > 0 })
        {
            _firstPtyOutputAtUtc ??= DateTime.UtcNow;

            Terminal.Write(chunk);

            // AC-34: while an agent is coupled to this pane, hand it the same bytes just rendered, so read_terminal
            // returns what happened since the coupling. Gated on IsCoupled so an uncoupled pane pays nothing.
            if (_viewModel?.PaneId is { Length: > 0 } paneId && _terminals is { } terminals && terminals.IsCoupled(paneId))
            {
                terminals.CaptureOutput(paneId, Encoding.UTF8.GetString(chunk));
            }

            // AC-75: output means the pty is still drawing (a thinking spinner ticking, text streaming), so the session
            // is visibly alive — keep its sidebar dot off a false "Done" while a long think/plan phase writes no
            // transcript line.
            _viewModel?.NotifyTerminalOutput();
        }

        // AC-760: checked on every tick (not only when a chunk arrived) so the 15s fallback still fires once the
        // CLI has gone quiet after its first burst without ever announcing bracketed paste.
        _CheckHostedTuiReadiness();
    }

    // The gate a held opening brief waits behind: `TerminalControl.BracketedPaste` flips true the moment the hosted
    // CLI sends DECSET 2004, which happens in the CLI's very first output burst (measured ~0.27s after spawn for
    // `claude` — AC-760 grooming). The fallback covers a CLI that never sends it.
    private void _CheckHostedTuiReadiness()
    {
        if (_hostedTuiReady)
        {
            return;
        }

        var fallbackElapsed = _firstPtyOutputAtUtc is { } since && DateTime.UtcNow - since >= HostedTuiReadyFallback;
        if (!Terminal.BracketedPaste && !fallbackElapsed)
        {
            return;
        }

        _hostedTuiReady = true;
        if (DataContext is TtyViewModel viewModel)
        {
            viewModel.MarkHostedTuiReady();
        }
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        _resizeSettle?.Stop();
        _resizeSettle = null;

        _outputFlush?.Stop();
        _outputFlush = null;

        _outputCancellation?.Cancel();
        _outputCancellation?.Dispose();
        _outputCancellation = null;

        // AC-34: the pane is gone (tab closed, shell exit). Deregister it so it drops out of list_terminals and any
        // agent coupled to it is decoupled automatically, with the surviving side told rather than left reading nothing.
        if (_terminals is { } terminals)
        {
            terminals.CouplingChanged -= OnCouplingChanged;
            if (_viewModel?.PaneId is { Length: > 0 } paneId)
            {
                terminals.PaneClosed(paneId);
            }
        }

        // Before the pty goes: the sink handed out at launch writes into it, and _WriteToPty returns silently once
        // it is null. Dropping the sink here is what turns CanTakeAPrompt false at the moment it stops being true,
        // rather than leaving a pane that accepts prompts into a closed process for the rest of its life.
        if (_viewModel is not null)
        {
            _viewModel.PromptSink = null;
        }

        _pty?.Dispose();
        _pty = null;
    }
}
