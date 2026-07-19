using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Threading;
using Cockpit.App.ViewModels;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.App.Plugins;

/// <summary>
/// The live <see cref="ICockpitSessionObserver"/> backing <c>ICockpitHost.Sessions</c>: the read/observe half
/// of the plugin surface. It tracks the cockpit's selected session (reporting its working directory and
/// raising <see cref="ActiveSessionChanged"/> when the selection or that directory changes) and relays every
/// session's produced output text to <see cref="OutputProduced"/>. One shared instance serves all plugins,
/// mirroring the single shared <see cref="ICockpitActions"/>. All events are marshalled to the UI thread so a
/// plugin's handler can touch its controls directly.
/// </summary>
internal sealed class PluginSessionObserver : ICockpitSessionObserver
{
    private readonly CockpitViewModel _cockpit;

    // The sessions we have hooked, so we can detach cleanly when one leaves the collection (no leaked handlers
    // on closed sessions) and avoid double-hooking on a spurious reset. The value is that session's own
    // RateLimits handler, kept so it can be unsubscribed with the exact delegate it was added with (a per-session
    // closure that raises usage-changed only while that session is the selected one).
    private readonly Dictionary<SessionPanelViewModel, NotifyCollectionChangedEventHandler> _hooked = [];

    public PluginSessionObserver(CockpitViewModel cockpit)
    {
        _cockpit = cockpit;
        _cockpit.PropertyChanged += _OnCockpitPropertyChanged;
        _cockpit.Sessions.CollectionChanged += _OnSessionsChanged;

        foreach (var session in _cockpit.Sessions)
        {
            _Hook(session);
        }
    }

    public string? ActiveSessionWorkingDirectory => _cockpit.SelectedSession?.WorkingDirectory;

    public string? ActivePaneId => _cockpit.SelectedSession?.PaneId;

    public SessionUsageSnapshot? ActiveSessionUsage => _Snapshot(_cockpit.SelectedSession);

    public event EventHandler? ActiveSessionChanged;

    public event EventHandler? ActiveSessionUsageChanged;

    public event EventHandler<SessionOutputText>? OutputProduced;

    // The selected session's ctx/5h/wk as a plugin reads it (AC-54), built from the same fields the header pill
    // renders — its context percentage and the self-labelled rate windows — carrying the profile label so a
    // per-profile history has something to group on. Null when nothing is selected; the windows map straight onto
    // the abstraction's PluginRateLimitWindow (the session's SessionRateWindow reports no span, so WindowMinutes is
    // left null).
    private static SessionUsageSnapshot? _Snapshot(SessionPanelViewModel? session)
    {
        if (session is null)
        {
            return null;
        }

        var windows = session.RateLimits
            .Select(window => new PluginRateLimitWindow(window.Label, window.UsedPercent, window.ResetsAt, WindowMinutes: null))
            .ToList();

        return new SessionUsageSnapshot(session.ActiveProfileLabel, session.ContextUsedPercent, windows);
    }

    private void _OnCockpitPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CockpitViewModel.SelectedSession))
        {
            _RaiseActiveSessionChanged();
            // A new selection is a new usage story, whether or not the working directory moved with it.
            _RaiseActiveSessionUsageChanged();
        }
    }

    private void _OnSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Reset (Clear) hands us no OldItems, so reconcile against the live collection instead of guessing.
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var session in _hooked.Keys.ToList())
            {
                if (!_cockpit.Sessions.Contains(session))
                {
                    _Unhook(session);
                }
            }

            foreach (var session in _cockpit.Sessions)
            {
                _Hook(session);
            }

            return;
        }

        foreach (var session in e.OldItems?.OfType<SessionPanelViewModel>() ?? [])
        {
            _Unhook(session);
        }

        foreach (var session in e.NewItems?.OfType<SessionPanelViewModel>() ?? [])
        {
            _Hook(session);
        }
    }

    private void _Hook(SessionPanelViewModel session)
    {
        if (_hooked.ContainsKey(session))
        {
            return;
        }

        // A rate window being added/cleared changes the usage snapshot as much as the context percentage does, but
        // it fires on the collection rather than as a property — so watch it too, and relay only while this session
        // is the selected one (a background session's windows do not touch the active-usage surface).
        void OnRateLimitsChanged(object? _, NotifyCollectionChangedEventArgs __)
        {
            if (ReferenceEquals(session, _cockpit.SelectedSession))
            {
                _RaiseActiveSessionUsageChanged();
            }
        }

        _hooked.Add(session, OnRateLimitsChanged);
        session.OutputTextProduced += _OnSessionOutput;
        session.PropertyChanged += _OnSessionPropertyChanged;
        session.RateLimits.CollectionChanged += OnRateLimitsChanged;
    }

    private void _Unhook(SessionPanelViewModel session)
    {
        if (!_hooked.Remove(session, out var onRateLimitsChanged))
        {
            return;
        }

        session.OutputTextProduced -= _OnSessionOutput;
        session.PropertyChanged -= _OnSessionPropertyChanged;
        session.RateLimits.CollectionChanged -= onRateLimitsChanged;
    }

    private void _OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _cockpit.SelectedSession))
        {
            return;
        }

        // The selected session learning its working directory (an SDK session's init event) is the same
        // "re-scope now" cue as the selection itself changing.
        if (e.PropertyName == nameof(SessionPanelViewModel.WorkingDirectory))
        {
            _RaiseActiveSessionChanged();
        }

        // The context percentage or the profile label moving is a fresh usage story for the same selection.
        if (e.PropertyName is nameof(SessionPanelViewModel.ContextUsedPercent) or nameof(SessionPanelViewModel.ActiveProfileLabel))
        {
            _RaiseActiveSessionUsageChanged();
        }
    }

    private void _OnSessionOutput(object? sender, string text)
    {
        if (sender is not SessionPanelViewModel session)
        {
            return;
        }

        var payload = new SessionOutputText(
            text,
            session.WorkingDirectory,
            ReferenceEquals(session, _cockpit.SelectedSession));

        _OnUiThread(() => OutputProduced?.Invoke(this, payload));
    }

    private void _RaiseActiveSessionChanged() =>
        _OnUiThread(() => ActiveSessionChanged?.Invoke(this, EventArgs.Empty));

    private void _RaiseActiveSessionUsageChanged() =>
        _OnUiThread(() => ActiveSessionUsageChanged?.Invoke(this, EventArgs.Empty));

    // Session events can originate off the UI thread (transcript tails, driver event loops); marshal so a
    // plugin handler runs where it can safely touch controls. Already-on-thread stays synchronous.
    private static void _OnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }
}
