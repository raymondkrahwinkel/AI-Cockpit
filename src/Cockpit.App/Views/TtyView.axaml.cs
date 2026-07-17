using System.ComponentModel;
using System.Diagnostics;
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
using Cockpit.Core.Abstractions.Toasts;
using Cockpit.Core.Toasts;
using Exclr8.Terminal;
using Exclr8.Terminal.Buffer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cockpit.App.Views;

/// <summary>
/// Hosts the real interactive <c>claude</c> TUI: an Exclr8 <see cref="TerminalControl"/> (a pure
/// byte-in/byte-out terminal-emulator renderer, no PTY plumbing of its own) bridged to a pty (ConPTY
/// on Windows, Porta.Pty on Linux/macOS via <c>IPtyHostFactory</c>). The code-behind owns the plumbing
/// between the two — pty output is written into the control, the control's <c>Input</c>/<c>Output</c>
/// byte events are written to pty stdin, and the control's <c>Resized</c> event is relayed to the pty
/// — because that bridge is inherently view/toolkit-bound, not view-model logic.
/// </summary>
public partial class TtyView : UserControl
{
    private IConPtyProcess? _pty;
    private CancellationTokenSource? _outputCancellation;
    private TtyViewModel? _viewModel;
    private TtyLaunchRequest? _pendingLaunch;
    private bool _launchPending;
    private bool _wired;
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

    // #58 confirmation logging: every Exclr8 Resized event and every pty.Resize call, so the net-zero
    // round-trip signature (>=2 Resized with different sizes within the settle window, followed by one
    // pty.Resize equal to the previous pty size) can be confirmed from %APPDATA%\Cockpit\logs\cockpit.log.
    // Resolved from the app's DI container rather than injected: this UserControl is constructed by the
    // XAML view locator/designer, not by the container, matching the existing Program.Services lookups in
    // App.axaml.cs. Skipped in the XAML previewer, where Program.Services is never assigned.
    private readonly ILogger<TtyView>? _logger =
        Design.IsDesignMode ? null : Program.Services.GetService<ILogger<TtyView>>();

    // AC-2 user feedback: a toast when claude's clipboard write (OSC 52) actually reaches the OS clipboard and
    // when a clicked link is handed to the browser, so the action is visibly acknowledged. Resolved from the app
    // container the same way as _logger (this control is built by the view locator, not the DI graph).
    private readonly IToastService? _toast =
        Design.IsDesignMode ? null : Program.Services.GetService<IToastService>();

    // #58 diagnostic instrumentation: throttles the per-keystroke TTY-DIAG log line (see
    // OnTerminalInputDiagnostics) to every KeyDiagThrottleEvery-th Input event, so a normal typing burst
    // doesn't flood the log while a reproduction (double-click + type) still has enough samples to show
    // the render-state jump.
    private const int KeyDiagThrottleEvery = 10;
    private int _keyDiagCounter;

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

        // #58 diagnostic instrumentation (observe-only, no behavior change): tunnel so the "before"
        // snapshot is captured before TerminalControl's own OnPointerPressed — which unconditionally sets
        // e.Handled = true and, on a double-click, mutates the buffer via SelectWord — has run. Reproduces
        // Rick's trigger exactly: double-click in the TTY + typing, no interaction outside the TTY.
        AddHandler(InputElement.PointerPressedEvent, OnTerminalPointerPressedDiagnostics, RoutingStrategies.Tunnel);

        // AC-2: Ctrl+click to follow a link. The terminal control forwards every click to the pty (and skips its
        // own link activation) whenever the running app has mouse reporting on — claude's TUI does — and offers no
        // modifier bypass, so a plain or Ctrl click never opened a URL. Tunnel so we hit-test the link and open it
        // before TerminalControl's own OnPointerPressed runs; Ctrl (not a plain click) so text selection by drag
        // is untouched. Handled only when a link is actually under the pointer, so every other click still reaches
        // claude. Not Windows-gated: the same Exclr8 control renders on every OS, so the gap and the fix are shared.
        AddHandler(InputElement.PointerPressedEvent, OnTerminalPointerPressedForLinks, RoutingStrategies.Tunnel);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.LaunchRequested -= OnLaunchRequested;
            _viewModel.VoiceTranscriptReady -= _OnVoiceTranscriptReady;
            _viewModel.PropertyChanged -= _OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as TtyViewModel;
        if (_viewModel is not null)
        {
            _viewModel.LaunchRequested += OnLaunchRequested;
            _viewModel.VoiceTranscriptReady += _OnVoiceTranscriptReady;
            _viewModel.PropertyChanged += _OnViewModelPropertyChanged;
            // The profile may already have been configured (dialog confirmed) before this view existed;
            // pull any pending launch now that we are subscribed. The VM's guard makes this fire once.
            _viewModel.TryRaiseLaunch();
        }

        WireTerminal();
        _ApplyTerminalFont();
    }

    /// <summary>
    /// TerminalControl.FontFamily/FontSize are plain CLR properties, not registered AvaloniaProperties,
    /// so they can't be targeted by a compiled XAML binding (#40) — applied here imperatively instead,
    /// both on attach and every time the global terminal-settings VM property changes, so Options →
    /// Terminal takes effect live. Both setters re-measure the cell and reflow the grid on assignment,
    /// which raises <see cref="Exclr8.Terminal.TerminalControl.Resized"/> if the new metrics change the
    /// column/row count — <see cref="OnTerminalResized"/> then resizes the pty to match, the same as a
    /// window resize.
    /// </summary>
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


    /// <summary>
    /// KeyDown for the push-to-talk hotkey — see the equivalent handler on <c>SessionView</c> for
    /// the guard reasoning. No-ops when global push-to-talk is active (see
    /// <see cref="PushToTalkKeyGate"/>) so the global coordinator's hold does not fire twice.
    /// </summary>
    private void _OnPushToTalkKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is { } vm
            && PushToTalkKeyGate.ShouldHandleLocally(e.Key, vm.PushToTalkKeyName, vm.GlobalPushToTalkEnabled)
            && vm.BeginVoiceHold())
        {
            e.Handled = true;
        }
    }

    /// <summary>KeyUp for the push-to-talk hotkey: ends the hold and transcribes without cleanup — see <see cref="TtyViewModel.OnVoiceTextReady"/>.</summary>
    private void _OnPushToTalkKeyUp(object? sender, KeyEventArgs e)
    {
        if (_viewModel is { } vm
            && PushToTalkKeyGate.ShouldHandleLocally(e.Key, vm.PushToTalkKeyName, vm.GlobalPushToTalkEnabled))
        {
            e.Handled = true;
            _ = vm.EndVoiceHoldAsync(applyCleanup: false);
        }
    }

    /// <summary>
    /// #58 diagnostic instrumentation: logs the Exclr8 render-state snapshot right before and right after
    /// a pointer press on the terminal, with <see cref="PointerPressedEventArgs.ClickCount"/> so a
    /// double-click is visible in the log. The "after" snapshot is captured via a
    /// <see cref="Dispatcher.UIThread"/> post rather than read synchronously here, so it reflects the state
    /// once TerminalControl's own (bubble-phase) OnPointerPressed handling — and any layout pass it
    /// triggers — has actually completed, not just the state at the moment this tunnel handler ran.
    /// Observe-only: never sets <c>e.Handled</c>, never touches the buffer.
    /// </summary>
    private void OnTerminalPointerPressedDiagnostics(object? sender, PointerPressedEventArgs e)
    {
        if (_logger is null)
        {
            return;
        }

        _logger.LogInformation(
            "TTY-DIAG [pointer] before (clickCount={ClickCount}): {Snapshot}",
            e.ClickCount, TtyDiagnosticsSnapshot.Capture(Terminal.Buffer));

        Dispatcher.UIThread.Post(() => _logger.LogInformation(
            "TTY-DIAG [pointer] after (clickCount={ClickCount}): {Snapshot}",
            e.ClickCount, TtyDiagnosticsSnapshot.Capture(Terminal.Buffer)));
    }

    /// <summary>
    /// #58 diagnostic instrumentation: logs the Exclr8 render-state snapshot on a throttled sample of the
    /// keystrokes the view forwards to the pty (see <see cref="KeyDiagThrottleEvery"/>) — enough samples to
    /// see the render state jump during a reproduction without flooding the log on a normal typing burst.
    /// Subscribed alongside (not instead of) <see cref="OnTerminalBytesToPty"/> in <see cref="WireTerminal"/>
    /// — observe-only, does not participate in writing bytes to the pty.
    /// </summary>
    private void OnTerminalInputDiagnostics(object? sender, ReadOnlyMemory<byte> e)
    {
        if (_logger is null)
        {
            return;
        }

        _keyDiagCounter++;
        if (_keyDiagCounter % KeyDiagThrottleEvery != 1)
        {
            return;
        }

        _logger.LogInformation(
            "TTY-DIAG [key] input #{Count} ({ByteCount} bytes): {Snapshot}",
            _keyDiagCounter, e.Length, TtyDiagnosticsSnapshot.Capture(Terminal.Buffer));
    }

    /// <summary>Writes a finished voice transcript as raw bytes into the pty's stdin — the same path a typed keystroke takes (<see cref="OnTerminalBytesToPty"/>).</summary>
    private void _OnVoiceTranscriptReady(string text)
    {
        var pty = _pty;
        if (pty is null)
        {
            return;
        }

        try
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            pty.InputStream.Write(bytes);
            pty.InputStream.Flush();
        }
        catch (Exception)
        {
            // The pty may have exited between the transcript arriving and the write; the output pump
            // already observes the exit and updates status, same as a dropped keystroke write.
        }
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
        // #58 diagnostics: separate subscriber, observe-only (see OnTerminalInputDiagnostics) — Input only,
        // not Output, since the goal is to see render state around keys the *user* sends, not protocol
        // replies the terminal generates on its own.
        Terminal.Input += OnTerminalInputDiagnostics;
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

    /// <summary>
    /// claude asked to write to the OS clipboard (OSC 52). Honour it against the real system clipboard and
    /// acknowledge it with a toast, so the copy the TUI reports is one the operator can see actually happened.
    /// </summary>
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

    /// <summary>
    /// Ctrl+click over a link when the terminal cannot activate it itself (mouse reporting is on, so the click is
    /// otherwise forwarded to claude). Hit-tests the cell under the pointer for an OSC 8 or provider-detected URL
    /// and opens it, consuming the click so it never reaches the pty.
    /// </summary>
    private void OnTerminalPointerPressedForLinks(object? sender, PointerPressedEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            return;
        }

        var pointer = e.GetCurrentPoint(Terminal);
        if (!pointer.Properties.IsLeftButtonPressed || _LinkAt(pointer.Position) is not { } url)
        {
            return;
        }

        if (_TryOpenLink(url))
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// The URL clickable at <paramref name="position"/> (pointer coordinates relative to the terminal), or null.
    /// Mirrors TerminalControl's own hit-test (OSC 8 cell first, then registered plain-URL providers) using its
    /// public buffer API; only the pixel-to-cell mapping (<c>GridPos</c>) is not public, so it is reached by
    /// reflection and any miss degrades to "no link", never a throw.
    /// </summary>
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

    /// <summary>Opens an http/https URL in the OS browser with a toast; returns whether it was handled (a browsable URL).</summary>
    private bool _TryOpenLink(string url)
    {
        if (!_IsBrowsableLink(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            _toast?.Show($"Opening {uri.Host} in your browser", ToastSeverity.Information);
        }
        catch (Exception ex)
        {
            // Best-effort: a failed browser launch must not crash the UI thread (mirrors MarkdownView._OpenUrl).
            _logger?.LogDebug(ex, "TTY hyperlink launch failed for {Url}", uri.AbsoluteUri);
            _toast?.Show("Could not open the link", ToastSeverity.Warning);
        }

        return true;
    }

    /// <summary>The <see cref="TerminalControl.LinkActivationPolicy"/> gate: only http/https URLs are handed to the browser.</summary>
    private static bool _IsBrowsableLink(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    // AC-2 link hit-test: pixel→cell is TerminalControl.GridPos, which is not public. Cached once; a null here (an
    // Exclr8 version that renamed it) simply means Ctrl+click stops opening links, never a crash.
    private static readonly System.Reflection.MethodInfo? _GridPosMethod = typeof(TerminalControl)
        .GetMethod("GridPos", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    /// <summary>
    /// A profile and its start defaults have been resolved. The pty can only be spawned once the
    /// terminal has a real size, so remember the request and launch on the next
    /// <see cref="OnTerminalResized"/> (or now if a size is already known).
    /// </summary>
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

    /// <summary>
    /// Forces the TUI to repaint: shrinks the pty a couple of rows, waits for claude to react to the resize
    /// (SIGWINCH), then restores the real size so claude re-renders its managed UI. Recovers the reflow
    /// glitch where claude's frames end up stacked at the top after a resize/focus change on some setups —
    /// fired deterministically by the resize-settle timer's net-zero-round-trip decision (#58,
    /// <see cref="TtyResizeSettleDecision"/>), by the #55 auto-redraw vangnet for a pure focus/activation
    /// event with no resize transient at all, and manually via the Redraw button. Does not clear the
    /// emulator (that would wipe the scrolled-back conversation claude never re-emits).
    /// </summary>
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

            var pty = _pendingLaunch.Launcher.Launch(
                _pendingLaunch.Provider,
                _pendingLaunch.Profile,
                _pendingLaunch.Options,
                (short)_lastColumns,
                (short)_lastRows,
                _pendingLaunch.WorkingDirectory,
                _pendingLaunch.Resume,
                // AC-13: the pane id becomes COCKPIT_PANE_ID in the CLI's environment, so the agent can set its own statusline.
                _viewModel?.PaneId);
            _pty = pty;
            _ptyColumns = _lastColumns;
            _ptyRows = _lastRows;

            // The session's own limits (context window, five-hour and weekly allowance) land in the file its
            // statusline writes; the launched process is what knows which file that is.
            if (pty is ITtyStatusFile { StatusFile: { } statusFile } && DataContext is TtyViewModel viewModel)
            {
                viewModel.TrackLimits(statusFile);
            }
            // The pty owns the process, so the view is where the meter (#78) learns which one this session is.
            if (_viewModel is not null)
            {
                _viewModel.ProcessId = pty.ProcessId;
            }
            _logger?.LogInformation("pty launched at {Columns}x{Rows}", _ptyColumns, _ptyRows);
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

        try
        {
            pty.InputStream.Write("\u001b\r"u8);
            pty.InputStream.Flush();
        }
        catch (Exception)
        {
            // The pty may have exited; the output pump reports that. Losing a keystroke to a dead session is not
            // something to take the cockpit down for.
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

        try
        {
            pty.InputStream.Write(e.Span);
            pty.InputStream.Flush();
        }
        catch (Exception)
        {
            // The pty may have exited between the input event and the write; the output pump will
            // observe the exit and update status.
        }
    }

    /// <summary>
    /// Terminal-surface mouse wheel: dispatches per <see cref="TtyWheelScrollGate"/> instead of letting
    /// <c>TerminalControl</c>'s own wheel handling run unconditionally.
    /// <see cref="TtyWheelScrollAction.ForwardArrowKeys"/> (#56) — alternate screen, no mouse tracking:
    /// Exclr8.Terminal's alternate screen keeps no scrollback, so this sends an Up/Down arrow-key press to
    /// the pty instead (mirrors xterm's alternateScroll). <see cref="TtyWheelScrollAction.NativeScroll"/>
    /// (#57) — primary/inline screen, which is what Claude Code's TUI actually renders on (capture-
    /// confirmed: no alt-screen escape anywhere): scrolls Exclr8's own primary-screen scrollback directly
    /// via <c>TerminalBuffer.ScrollViewUp</c>/<c>ScrollViewDown</c>, which is real and populated (only the
    /// alternate screen's <c>ScrollbackLimit</c> is zeroed). Both mark the event handled so
    /// <c>TerminalControl</c>'s own <c>OnPointerWheelChanged</c> does not also run.
    /// <see cref="TtyWheelScrollAction.PassThrough"/> — alt screen with mouse tracking requested: left
    /// alone, <c>TerminalControl</c>'s own SGR-mouse-report path already covers it.
    /// </summary>
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

                try
                {
                    var bytes = TtyWheelScrollGate.EncodeArrowKey(e.Delta.Y > 0, buffer.ApplicationCursorKeys);
                    pty.InputStream.Write(bytes);
                    pty.InputStream.Flush();
                }
                catch (Exception)
                {
                    // The pty may have exited; the output pump already handles that.
                }

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
            _viewModel?.OnProcessExited();
        });
    }

    // Writes everything the pty reader has accumulated in one Terminal.Write, on the UI thread. Driven by the
    // ~30 fps flush timer (and once more on exit) so the terminal repaints at a bounded rate under a burst — see
    // the _outputFlush field comment. A no-op when nothing is pending, so an idle session costs nothing.
    private void _FlushOutput()
    {
        byte[] chunk;
        lock (_outputLock)
        {
            if (_outputPending.Count == 0)
            {
                return;
            }

            chunk = [.. _outputPending];
            _outputPending.Clear();
        }

        Terminal.Write(chunk);
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

        _pty?.Dispose();
        _pty = null;
    }
}
