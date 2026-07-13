using System.ComponentModel;
using Avalonia.Threading;
using Cockpit.App.ViewModels;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.App.Plugins;

/// <summary>
/// The <see cref="IPluginSessionContext"/> handed to a header item: one session panel, exposed as the little a
/// plugin needs to follow it — where it is working, and what it produces. Bound to that one session for the life
/// of its panel, unlike <see cref="PluginSessionObserver"/>, which tracks whichever session is selected. Events
/// are marshalled to the UI thread so a handler can touch its controls directly; disposing detaches, so a closed
/// session leaves no handler behind on a panel nobody can see.
/// </summary>
internal sealed class PluginSessionContext : IPluginSessionContext, IDisposable
{
    private readonly SessionPanelViewModel _session;

    public PluginSessionContext(SessionPanelViewModel session)
    {
        _session = session;
        _session.OutputTextProduced += _OnOutput;
        _session.PropertyChanged += _OnSessionPropertyChanged;
    }

    public string PaneId => _session.PaneId;

    public string? WorkingDirectory => _session.WorkingDirectory;

    public event EventHandler? WorkingDirectoryChanged;

    public event EventHandler<SessionOutputText>? OutputProduced;

    public void Dispose()
    {
        _session.OutputTextProduced -= _OnOutput;
        _session.PropertyChanged -= _OnSessionPropertyChanged;
    }

    private void _OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionPanelViewModel.WorkingDirectory))
        {
            _OnUiThread(() => WorkingDirectoryChanged?.Invoke(this, EventArgs.Empty));
        }
    }

    private void _OnOutput(object? sender, string text)
    {
        // IsFromActiveSession is true by definition here: this context *is* the session that produced it, which
        // is the whole point of a per-session context over the selection-following observer.
        var payload = new SessionOutputText(text, _session.WorkingDirectory, IsFromActiveSession: true);
        _OnUiThread(() => OutputProduced?.Invoke(this, payload));
    }

    // Session events can originate off the UI thread (transcript tails, driver event loops).
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
