using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
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
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.LaunchRequested -= OnLaunchRequested;
        }

        _viewModel = DataContext as ClaudeTtyViewModel;
        if (_viewModel is not null)
        {
            _viewModel.LaunchRequested += OnLaunchRequested;
            // The profile may already have been configured (dialog confirmed) before this view existed;
            // pull any pending launch now that we are subscribed. The VM's guard makes this fire once.
            _viewModel.TryRaiseLaunch();
        }

        WireTerminal();
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

        if (_launchPending)
        {
            StartPty();
            return;
        }

        _pty?.Resize((short)_lastColumns, (short)_lastRows);
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
