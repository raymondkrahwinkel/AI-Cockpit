using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Claude;
using Cockpit.Core.Profiles;
using Exclr8.Terminal;
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
public partial class ClaudeTtyView : UserControl
{
    private IConPtyProcess? _pty;
    private CancellationTokenSource? _outputCancellation;
    private ClaudeTtyViewModel? _viewModel;
    private IClaudeTtyLauncher? _pendingLauncher;
    private ClaudeProfile? _pendingProfile;
    private string? _pendingPermissionMode;
    private string? _pendingModel;
    private string? _pendingEffort;
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

    // #58 confirmation logging: every Exclr8 Resized event and every pty.Resize call, so the net-zero
    // round-trip signature (>=2 Resized with different sizes within the settle window, followed by one
    // pty.Resize equal to the previous pty size) can be confirmed from %APPDATA%\Cockpit\logs\cockpit.log.
    // Resolved from the app's DI container rather than injected: this UserControl is constructed by the
    // XAML view locator/designer, not by the container, matching the existing Program.Services lookups in
    // App.axaml.cs. Skipped in the XAML previewer, where Program.Services is never assigned.
    private readonly ILogger<ClaudeTtyView>? _logger =
        Design.IsDesignMode ? null : Program.Services.GetService<ILogger<ClaudeTtyView>>();

    // Auto-redraw (#55): debounces a burst of redraw triggers (focus, activation, visibility) into a
    // single ForceRedraw() once they stop arriving, same restart-on-trigger approach as _resizeSettle
    // above, just a separate timer/concern.
    private DispatcherTimer? _autoRedrawDebounce;
    private WindowBase? _activatedWindow;

    public ClaudeTtyView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;

        // Push-to-talk (F9 by default): tunnel so we intercept it before the Terminal control's own
        // KeyDown handling would otherwise encode it as a VT keystroke and send it into the pty.
        AddHandler(InputElement.KeyDownEvent, _OnPushToTalkKeyDown, RoutingStrategies.Tunnel);
        AddHandler(InputElement.KeyUpEvent, _OnPushToTalkKeyUp, RoutingStrategies.Tunnel);

        // Auto-redraw (#55): Rick confirmed the existing manual Redraw button (ForceRedraw(), see below)
        // recovers a TUI that renders desynced after the user does something outside the TTY (a dialog
        // button, a focus change) — intermittent on his Fedora/KDE/Wayland setup. Firing it automatically
        // on the same signals that correlate with the glitch means he never has to notice and click it.
        // ForceRedraw() is non-destructive (a two-step pty resize, no emulator clear), so auto-firing it
        // costs nothing when the render was already fine.
        Terminal.GotFocus += OnTerminalGotFocus;
        PropertyChanged += OnControlPropertyChanged;

        // Scrollback fallback for the alt screen (#56): tunnel so we intercept before TerminalControl's
        // own OnPointerWheelChanged, which otherwise no-ops the notch whenever the TUI hasn't requested
        // mouse tracking — see OnTerminalWheel/TtyWheelScrollGate.
        AddHandler(InputElement.PointerWheelChangedEvent, OnTerminalWheel, RoutingStrategies.Tunnel);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.LaunchRequested -= OnLaunchRequested;
            _viewModel.VoiceTranscriptReady -= _OnVoiceTranscriptReady;
            _viewModel.PropertyChanged -= _OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as ClaudeTtyViewModel;
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
        _ApplyHeaderLayout();
    }

    /// <summary>
    /// Auto-redraw trigger (#55): the window hosting this pane was activated — covers switching back to
    /// the app from elsewhere, and an owned modal dialog closing and handing activation back to the main
    /// window. Subscribed here (not the constructor) because the control isn't attached to a
    /// <see cref="TopLevel"/> yet when constructed.
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (TopLevel.GetTopLevel(this) is WindowBase window)
        {
            _activatedWindow = window;
            window.Activated += OnWindowActivated;
        }

        // The pane itself just entered the visual tree (e.g. a freshly created session, or reparented
        // back after being hidden) — another of the #55 auto-redraw triggers.
        ScheduleAutoRedraw();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        if (_activatedWindow is not null)
        {
            _activatedWindow.Activated -= OnWindowActivated;
            _activatedWindow = null;
        }
    }

    private void OnWindowActivated(object? sender, EventArgs e) => ScheduleAutoRedraw();

    private void OnTerminalGotFocus(object? sender, FocusChangedEventArgs e) => ScheduleAutoRedraw();

    /// <summary>Auto-redraw trigger (#55): the TTY pane was re-selected/became visible again — e.g. switching back to it in single-session or zoomed layout, where the other panels are collapsed via <c>IsVisible</c> rather than removed.</summary>
    private void OnControlPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.IsVisibleProperty && e.NewValue is true)
        {
            ScheduleAutoRedraw();
        }
    }

    /// <summary>
    /// Debounces the auto-redraw triggers above into a single <see cref="ForceRedraw"/> once they stop
    /// arriving for the debounce window — a single user gesture (closing a dialog) can raise several in
    /// quick succession (focus regained, window activated), and each would otherwise independently kick
    /// off the two-step pty resize. <see cref="TtyAutoRedrawGate"/> guards against scheduling before
    /// there is a running pty with a known size to redraw.
    /// </summary>
    private void ScheduleAutoRedraw()
    {
        if (!TtyAutoRedrawGate.ShouldScheduleRedraw(
                _pty is not null, _lastColumns, _lastRows, _resizeSettle is { IsEnabled: true }))
        {
            return;
        }

        _autoRedrawDebounce ??= CreateAutoRedrawDebounceTimer();
        _autoRedrawDebounce.Stop();
        _autoRedrawDebounce.Start();
    }

    private DispatcherTimer CreateAutoRedrawDebounceTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            ForceRedraw();
        };

        return timer;
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
        if (e.PropertyName is nameof(ClaudeTtyViewModel.TerminalFontFamily) or nameof(ClaudeTtyViewModel.TerminalFontSize))
        {
            _ApplyTerminalFont();
        }
        else if (e.PropertyName == nameof(ClaudeTtyViewModel.IsVerticalLayout))
        {
            _ApplyHeaderLayout();
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
    /// Re-docks the header beside the terminal in stacked-vertical layout instead of above it (#54):
    /// applied imperatively, same reasoning as <see cref="_ApplyTerminalFont"/> — <c>DockPanel.Dock</c>
    /// and the status row's <c>Orientation</c> both need to flip together with
    /// <see cref="ClaudeTtyViewModel.IsVerticalLayout"/>, and a converter pair for two properties driven
    /// by one bool is more machinery than just setting them here on attach and on every VM change.
    /// Stacked panels are wide and short, so a left-hand header column reads better there than a
    /// top-docked one that eats into the little height each panel gets; the normal grid keeps it on top.
    /// </summary>
    private void _ApplyHeaderLayout()
    {
        if (_viewModel is null)
        {
            return;
        }

        if (_viewModel.IsVerticalLayout)
        {
            DockPanel.SetDock(HeaderPanel, Dock.Left);
            HeaderPanel.Margin = new Thickness(0, 0, 8, 0);
            StatusRow.Orientation = Orientation.Vertical;
        }
        else
        {
            DockPanel.SetDock(HeaderPanel, Dock.Top);
            HeaderPanel.Margin = new Thickness(0, 0, 0, 6);
            StatusRow.Orientation = Orientation.Horizontal;
        }
    }

    /// <summary>
    /// KeyDown for the push-to-talk hotkey — see the equivalent handler on <c>ClaudeSessionView</c> for
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

    /// <summary>KeyUp for the push-to-talk hotkey: ends the hold and transcribes without cleanup — see <see cref="ClaudeTtyViewModel.OnVoiceTextReady"/>.</summary>
    private void _OnPushToTalkKeyUp(object? sender, KeyEventArgs e)
    {
        if (_viewModel is { } vm
            && PushToTalkKeyGate.ShouldHandleLocally(e.Key, vm.PushToTalkKeyName, vm.GlobalPushToTalkEnabled))
        {
            e.Handled = true;
            _ = vm.EndVoiceHoldAsync(applyCleanup: false);
        }
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
        Terminal.Output += OnTerminalBytesToPty;
        Terminal.Resized += OnTerminalResized;
    }

    /// <summary>
    /// A profile and its start defaults have been resolved. The pty can only be spawned once the
    /// terminal has a real size, so remember the request and launch on the next
    /// <see cref="OnTerminalResized"/> (or now if a size is already known).
    /// </summary>
    private void OnLaunchRequested(
        IClaudeTtyLauncher launcher,
        ClaudeProfile? profile,
        string? permissionMode,
        string? model,
        string? effort)
    {
        _pendingLauncher = launcher;
        _pendingProfile = profile;
        _pendingPermissionMode = permissionMode;
        _pendingModel = model;
        _pendingEffort = effort;
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
        if (!_launchPending || _pendingLauncher is null || _lastColumns <= 0 || _lastRows <= 0)
        {
            return;
        }

        _launchPending = false;

        try
        {
            // Recommended before connecting a freshly-spawned pty: clears any dimension-detection
            // races from the app's own startup so they don't leave stacked partial renders behind.
            Terminal.PrepareForNewSession();

            _pty = _pendingLauncher.Launch(
                _pendingProfile,
                _pendingPermissionMode,
                _pendingModel,
                _pendingEffort,
                (short)_lastColumns,
                (short)_lastRows);
            _ptyColumns = _lastColumns;
            _ptyRows = _lastRows;
            _logger?.LogInformation("pty launched at {Columns}x{Rows}", _ptyColumns, _ptyRows);
        }
        catch (Exception ex)
        {
            Terminal.Write(Encoding.UTF8.GetBytes($"\r\nFailed to launch TUI: {ex.Message}\r\n"));
            _viewModel?.OnLaunchFailed();
            return;
        }

        _outputCancellation = new CancellationTokenSource();
        _ = PumpOutputAsync(_pty, _outputCancellation.Token);
        _viewModel?.OnLaunchSucceeded();
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
    /// Scrollback fallback for the alt screen (#56). Exclr8.Terminal's alternate screen keeps no
    /// scrollback, so TerminalControl's own wheel handling is a no-op there unless the TUI requested
    /// mouse tracking — Claude Code's TUI does neither. When <see cref="TtyWheelScrollGate"/> says so,
    /// this sends an Up/Down arrow-key press to the pty instead (mirrors xterm's alternateScroll) and
    /// marks the event handled so TerminalControl's own <c>OnPointerWheelChanged</c> does not also run.
    /// Left alone otherwise: the primary screen's native pixel-scroll scrollback and the alt screen's own
    /// SGR-mouse-report path (when the TUI did request tracking) both still work exactly as before.
    /// </summary>
    private void OnTerminalWheel(object? sender, PointerWheelEventArgs e)
    {
        var pty = _pty;
        if (pty is null)
        {
            return;
        }

        var buffer = Terminal.Buffer;
        if (!TtyWheelScrollGate.ShouldForwardAsArrowKeys(buffer.IsAltScreen, buffer.MouseMode))
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

                var chunk = new byte[read];
                Array.Copy(buffer, chunk, read);
                await Dispatcher.UIThread.InvokeAsync(() => Terminal.Write(chunk));
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

        await Dispatcher.UIThread.InvokeAsync(() => _viewModel?.OnProcessExited());
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        _resizeSettle?.Stop();
        _resizeSettle = null;

        _autoRedrawDebounce?.Stop();
        _autoRedrawDebounce = null;

        _outputCancellation?.Cancel();
        _outputCancellation?.Dispose();
        _outputCancellation = null;

        _pty?.Dispose();
        _pty = null;
    }
}
