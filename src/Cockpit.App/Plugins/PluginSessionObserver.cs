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
    // on closed sessions) and avoid double-hooking on a spurious reset.
    private readonly HashSet<SessionPanelViewModel> _hooked = [];

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

    public event EventHandler? ActiveSessionChanged;

    public event EventHandler<SessionOutputText>? OutputProduced;

    private void _OnCockpitPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CockpitViewModel.SelectedSession))
        {
            _RaiseActiveSessionChanged();
        }
    }

    private void _OnSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Reset (Clear) hands us no OldItems, so reconcile against the live collection instead of guessing.
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var session in _hooked.ToList())
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
        if (_hooked.Add(session))
        {
            session.OutputTextProduced += _OnSessionOutput;
            session.PropertyChanged += _OnSessionPropertyChanged;
        }
    }

    private void _Unhook(SessionPanelViewModel session)
    {
        if (_hooked.Remove(session))
        {
            session.OutputTextProduced -= _OnSessionOutput;
            session.PropertyChanged -= _OnSessionPropertyChanged;
        }
    }

    private void _OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The selected session learning its working directory (an SDK session's init event) is the same
        // "re-scope now" cue as the selection itself changing.
        if (e.PropertyName == nameof(SessionPanelViewModel.WorkingDirectory)
            && ReferenceEquals(sender, _cockpit.SelectedSession))
        {
            _RaiseActiveSessionChanged();
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
