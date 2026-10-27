using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Layout;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cockpit.App.Docking;

// AC-1751: a window per floating workspace (mockup AC-1749 v003, flow 8), on the `DockPanelHost` pattern.
internal sealed class WorkspaceWindowHost
{
    private readonly WorkspacesViewModel _workspaces;
    private readonly Func<string, Task> _closeWorkspace;
    private readonly Func<IWindowBoundsStore?> _boundsStore;
    private readonly Dictionary<string, DockPanelWindow> _windows = new(StringComparer.Ordinal);

    // Windows open once the cockpit's own view stands (see `Start`), never before.
    private bool _started;

    private bool _watchingMainWindow;

    // Set while the cockpit itself closes: its workspace windows go with it and stay floating for the next start.
    private bool _closingWithTheCockpit;

    internal WorkspaceWindowHost(WorkspacesViewModel workspaces, Func<string, Task> closeWorkspace, Func<IWindowBoundsStore?> boundsStore)
    {
        _workspaces = workspaces;
        _closeWorkspace = closeWorkspace;
        _boundsStore = boundsStore;
        workspaces.PropertyChanged += _OnWorkspacesChanged;
        workspaces.FloatingWorkspaceShown += (_, workspaceId) =>
        {
            if (_windows.GetValueOrDefault(workspaceId) is { } window)
            {
                WindowActivation.BringToFront(window);
            }
        };
    }

    // Test seam: the window a workspace stands in right now, null while it has a tab.
    internal DockPanelWindow? WindowFor(string workspaceId) => _windows.GetValueOrDefault(workspaceId);

    internal void Start()
    {
        _started = true;
        _Reconcile();
    }

    // The window lets go of the body first, so the strip can take the same instance back.
    internal async Task DockBackAsync(string workspaceId)
    {
        if (_windows.Remove(workspaceId, out var window))
        {
            window.ReleaseView();
            window.Close();
        }

        await _workspaces.DockBackAsync(workspaceId).ConfigureAwait(true);
    }

    // Closing the cockpit is not Dock back: the windows close, the workspaces stay floating.
    internal void CloseWithTheCockpit()
    {
        _closingWithTheCockpit = true;
        foreach (var window in _windows.Values.ToList())
        {
            window.Close();
        }
    }

    private void _OnWorkspacesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WorkspacesViewModel.Settings) or nameof(WorkspacesViewModel.AvailablePluginWorkspaceTypes))
        {
            _Reconcile();
        }
    }

    // Closes the windows of workspaces that are gone or docked, and opens one for each floating workspace without.
    private void _Reconcile()
    {
        if (!_started || _closingWithTheCockpit)
        {
            return;
        }

        var workspaces = _workspaces.Settings.Workspaces;
        foreach (var (workspaceId, window) in _windows.ToList())
        {
            if (!workspaces.Any(workspace => workspace.Id == workspaceId && workspace.IsFloating))
            {
                _windows.Remove(workspaceId);
                window.ReleaseView();
                window.Close();
            }
        }

        foreach (var workspace in workspaces.Where(workspace => workspace.IsFloating && !_windows.ContainsKey(workspace.Id)))
        {
            if (_workspaces.AvailablePluginWorkspaceTypes.All(type => type.Id != workspace.Type.Id))
            {
                continue;
            }

            // A type that stopped offering a window comes back to the strip rather than vanishing from both.
            if (!_workspaces.CanFloat(workspace))
            {
                _ = _LoggedAsync(() => _workspaces.DockBackAsync(workspace.Id), "Docking back a workspace that can no longer float failed.");
                continue;
            }

            if (_workspaces.BodyFor(workspace) is { } body)
            {
                _Open(workspace.Id, workspace.Name, body);
            }
        }
    }

    private void _Open(string workspaceId, string title, Control body)
    {
        var window = new DockPanelWindow(workspaceId, title, body, _boundsStore(), "Put this workspace back in the tab strip");
        window.DockBackRequested += async (_, _) =>
            await _LoggedAsync(() => DockBackAsync(workspaceId), "Docking a workspace back from its window failed.").ConfigureAwait(true);

        // The window's own ✕ is the tab's ✕: it asks first, and the window closes only once the workspace is gone.
        window.Closing += (_, e) =>
        {
            if (!_closingWithTheCockpit && e.CloseReason == WindowCloseReason.WindowClosing && _windows.ContainsKey(workspaceId))
            {
                e.Cancel = true;
                _ = _LoggedAsync(() => _closeWorkspace(workspaceId), "Closing a floating workspace failed.");
            }
        };
        window.Closed += (_, _) =>
        {
            _windows.Remove(workspaceId);
            window.ReleaseView();
        };
        _windows[workspaceId] = window;

        // Ownerless like a floating dock panel, so it can stand behind the cockpit; closed with it by hand.
        if (!_watchingMainWindow && _MainWindow() is { } main)
        {
            main.Closed += (_, _) => CloseWithTheCockpit();
            _watchingMainWindow = true;
        }

        WindowActivation.BringToFront(window);
    }

    // The window's events cannot await, so a failed move or write is logged, never lost.
    private static async Task _LoggedAsync(Func<Task> work, string failure)
    {
        try
        {
            await work().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Program.Services?.GetService<ILogger<WorkspaceWindowHost>>()?.LogWarning(exception, failure);
        }
    }

    private static Window? _MainWindow() =>
        Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } main }
            ? main
            : null;
}
