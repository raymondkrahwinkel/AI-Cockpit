using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Plugins.Abstractions.Docking;

namespace Cockpit.App.Docking;

// AC-1710: the one place that decides where a dock panel stands — in the rail or in its own window — and moves its
// view there. Old host lets go before the new one takes it: one control has one visual parent, and the assistant's
// leaving view hands its scroll position to the arriving one only in that order (AC-953).
public sealed class DockPanelHost
{
    private readonly CockpitViewModel _cockpit;
    private readonly IDockPanelRegistry? _registry;
    private readonly Func<IWindowBoundsStore?> _boundsStore;
    private readonly Dictionary<string, DockPanelWindow> _windows = [];
    private readonly Dictionary<string, (Action Show, Action Close)> _ownWindows = [];

    // Floating windows reopen once the cockpit's own view stands (see `RestoreFloatingWindows`), never before.
    private bool _restoring;

    private bool _watchingMainWindow;

    // Set while the cockpit itself closes: its floating windows go with it, and must not read that as "dock me back".
    private bool _closingWithTheCockpit;

    internal DockPanelHost(CockpitViewModel cockpit, IDockPanelRegistry? registry, Func<IWindowBoundsStore?> boundsStore)
    {
        _cockpit = cockpit;
        _registry = registry;
        _boundsStore = boundsStore;

        if (registry is not null)
        {
            registry.Changed += (_, _) => RestoreIfStarted();
        }
    }

    // Test seam: the generic window a panel stands in right now, null while it is docked.
    internal Window? WindowFor(string panelId) => _windows.GetValueOrDefault(panelId);

    // Whether the rail draws the undock button for this panel: a panel with a window of its own brings its own.
    public bool OffersUndock(DockPanelRegistration panel) => panel.CanFloat && !_ownWindows.ContainsKey(panel.Id);

    // Host-internal, never on the plugin contract: the assistant keeps its own window (drag-to-dock, always-on-top),
    // while where it stands is still decided here. `show` opens or raises it; `close` takes it away.
    internal void UseOwnWindow(string panelId, Action show, Action close) => _ownWindows[panelId] = (show, close);

    public async Task ShowInAsync(string panelId, bool floating)
    {
        if (floating)
        {
            if (_cockpit.OpenDockPanelId == panelId)
            {
                _cockpit.OpenDockPanelId = null;
            }

            _cockpit.SetDockPanelFloating(panelId, true);
            await _cockpit.PersistDockStandAsync().ConfigureAwait(true);
            _ShowWindow(panelId);
            return;
        }

        // Unfloated first, so the window's own close below reads as a hand-over rather than the operator's ×.
        _cockpit.SetDockPanelFloating(panelId, false);
        _CloseWindow(panelId);
        _cockpit.OpenDockPanelId = panelId;
        await _cockpit.PersistDockStandAsync().ConfigureAwait(true);

        if (_MainWindow() is { } main)
        {
            WindowActivation.BringToFront(main);
        }
    }

    // A late registration, or a stand read back off disk, once the cockpit's view has started restoring.
    internal void RestoreIfStarted()
    {
        if (_restoring)
        {
            RestoreFloatingWindows();
        }
    }

    // Puts every floating panel that has no window yet into one: after a restart, and for a plugin registering late.
    internal void RestoreFloatingWindows()
    {
        _restoring = true;
        foreach (var panel in _registry?.Panels ?? [])
        {
            if (!_cockpit.IsDockPanelFloating(panel.Id) || _ownWindows.ContainsKey(panel.Id) || _windows.ContainsKey(panel.Id))
            {
                continue;
            }

            // A panel that stopped offering a window comes back to the rail rather than vanishing from both.
            if (!panel.CanFloat)
            {
                _cockpit.SetDockPanelFloating(panel.Id, false);
                continue;
            }

            _ShowWindow(panel.Id);
        }
    }

    private void _ShowWindow(string panelId)
    {
        if (_ownWindows.TryGetValue(panelId, out var own))
        {
            own.Show();
            return;
        }

        if (_windows.TryGetValue(panelId, out var standing))
        {
            WindowActivation.BringToFront(standing);
            return;
        }

        if (_registry is null || _registry.Panels.FirstOrDefault(panel => panel.Id == panelId) is not { } registration)
        {
            return;
        }

        var window = new DockPanelWindow(registration, _registry.ViewFor(registration), _boundsStore());
        window.DockBackRequested += (_, _) => _ = ShowInAsync(panelId, floating: false);
        window.Closed += (_, _) => _OnWindowClosed(panelId, window);
        _windows[panelId] = window;

        // Ownerless like the assistant's window, so it can stand behind the cockpit; closed with it by hand.
        if (!_watchingMainWindow && _MainWindow() is { } main)
        {
            main.Closed += _OnMainWindowClosed;
            _watchingMainWindow = true;
        }

        WindowActivation.BringToFront(window);
    }

    private void _CloseWindow(string panelId)
    {
        if (_ownWindows.TryGetValue(panelId, out var own))
        {
            own.Close();
            return;
        }

        _windows.GetValueOrDefault(panelId)?.Close();
    }

    // The window lets go of the view so the rail can take it. A close the operator made (×) docks the panel back to
    // its tab without opening the rail: a floating panel with no window would be reachable from nowhere.
    private void _OnWindowClosed(string panelId, DockPanelWindow window)
    {
        _windows.Remove(panelId);
        window.ReleaseView();

        if (!_closingWithTheCockpit && _cockpit.IsDockPanelFloating(panelId))
        {
            _cockpit.SetDockPanelFloating(panelId, false);
            _ = _cockpit.PersistDockStandAsync();
        }
    }

    private void _OnMainWindowClosed(object? sender, EventArgs e)
    {
        if (sender is Window main)
        {
            main.Closed -= _OnMainWindowClosed;
        }

        _closingWithTheCockpit = true;
        foreach (var window in _windows.Values.ToList())
        {
            window.Close();
        }
    }

    private static Window? _MainWindow() =>
        Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } main }
            ? main
            : null;
}
