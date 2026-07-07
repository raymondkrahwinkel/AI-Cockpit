using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using SvcSystems.UI.Terminal;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Claude;
using Cockpit.Core.Profiles;

namespace Cockpit.App.Views;

/// <summary>
/// Hosts the real interactive <c>claude</c> TUI: a <see cref="TerminalControl"/> (XTerm.NET renderer)
/// bridged to a pty (ConPTY on Windows, Porta.Pty on Linux/macOS via <c>IPtyHostFactory</c>). The
/// code-behind owns the plumbing between the two — pty output is fed to the
/// terminal model, terminal keystrokes are written to pty stdin, and terminal resizes are relayed to
/// the pty — because that bridge is inherently view/toolkit-bound, not view-model logic.
/// </summary>
public partial class ClaudeTtyView : UserControl
{
    private TerminalControlModel? _model;
    private IConPtyProcess? _pty;
    private CancellationTokenSource? _outputCancellation;
    private ClaudeTtyViewModel? _viewModel;
    private IClaudeTtyLauncher? _pendingLauncher;
    private ClaudeProfile? _pendingProfile;
    private string? _pendingPermissionMode;
    private string? _pendingModel;
    private string? _pendingEffort;
    private bool _launchPending;
    private short _lastColumns;
    private short _lastRows;

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

        EnsureModel();
    }

    private void EnsureModel()
    {
        if (_model is not null || Terminal.Model is not null)
        {
            _model ??= Terminal.Model;
            return;
        }

        _model = new TerminalControlModel();
        _model.SizeChanged += OnTerminalSizeChanged;
        _model.UserInput += OnTerminalUserInput;
        Terminal.Model = _model;
    }

    /// <summary>
    /// A profile and its start defaults have been resolved. The pty can only be spawned once the
    /// terminal has a real size, so remember the request and launch on the next
    /// <see cref="OnTerminalSizeChanged"/> (or now if a size is already known).
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

    private void OnTerminalSizeChanged(object? sender, TerminalSizeChangedEventArgs e)
    {
        _lastColumns = (short)Math.Max(1, e.Cols);
        _lastRows = (short)Math.Max(1, e.Rows);

        if (_launchPending)
        {
            StartPty();
            return;
        }

        _pty?.Resize(_lastColumns, _lastRows);
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
            _pty = _pendingLauncher.Launch(
                _pendingProfile,
                _pendingPermissionMode,
                _pendingModel,
                _pendingEffort,
                _lastColumns,
                _lastRows);
        }
        catch (Exception ex)
        {
            _model?.Feed($"\r\nFailed to launch TUI: {ex.Message}\r\n");
            _viewModel?.OnProcessExited();
            return;
        }

        _outputCancellation = new CancellationTokenSource();
        _ = PumpOutputAsync(_pty, _outputCancellation.Token);
    }

    private void OnTerminalUserInput(object? sender, TerminalUserInputEventArgs e)
    {
        var pty = _pty;
        if (pty is null)
        {
            return;
        }

        try
        {
            var data = e.Data;
            pty.InputStream.Write(data.Span);
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
                await Dispatcher.UIThread.InvokeAsync(() => _model?.Feed(chunk, read));
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
