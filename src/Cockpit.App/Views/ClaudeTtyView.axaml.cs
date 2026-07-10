using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Claude;
using Cockpit.Core.Profiles;
using Exclr8.Terminal;

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

    public ClaudeTtyView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;

        // Push-to-talk (F9 by default): tunnel so we intercept it before the Terminal control's own
        // KeyDown handling would otherwise encode it as a VT keystroke and send it into the pty.
        AddHandler(InputElement.KeyDownEvent, _OnPushToTalkKeyDown, RoutingStrategies.Tunnel);
        AddHandler(InputElement.KeyUpEvent, _OnPushToTalkKeyUp, RoutingStrategies.Tunnel);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.LaunchRequested -= OnLaunchRequested;
            _viewModel.VoiceTranscriptReady -= _OnVoiceTranscriptReady;
        }

        _viewModel = DataContext as ClaudeTtyViewModel;
        if (_viewModel is not null)
        {
            _viewModel.LaunchRequested += OnLaunchRequested;
            _viewModel.VoiceTranscriptReady += _OnVoiceTranscriptReady;
            // The profile may already have been configured (dialog confirmed) before this view existed;
            // pull any pending launch now that we are subscribed. The VM's guard makes this fire once.
            _viewModel.TryRaiseLaunch();
        }

        WireTerminal();
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

        if (_launchPending)
        {
            StartPty();
            return;
        }

        _pty?.Resize((short)_lastColumns, (short)_lastRows);
    }

    /// <summary>
    /// Forces the TUI to repaint from scratch: clears the emulator (screen + scrollback) and nudges the pty
    /// size so claude receives a resize (SIGWINCH) and re-renders. This is the manual recovery for the
    /// reflow/focus glitch where — after a window resize, alt-tab or display-scale change on some Linux
    /// setups — claude's frames end up stacked at the top with stale content showing through.
    /// </summary>
    private void ForceRedraw()
    {
        var pty = _pty;
        if (pty is null || _lastColumns <= 0 || _lastRows <= 0)
        {
            return;
        }

        // ESC[3J clears scrollback, ESC[2J the screen, ESC[H homes the cursor — wipe any stale frames first.
        Terminal.Write(Encoding.UTF8.GetBytes("[3J[2J[H"));
        try
        {
            pty.Resize((short)Math.Max(1, _lastColumns - 1), (short)_lastRows);
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
        _outputCancellation?.Cancel();
        _outputCancellation?.Dispose();
        _outputCancellation = null;

        _pty?.Dispose();
        _pty = null;
    }
}
