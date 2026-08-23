using System.ComponentModel;
using Avalonia.Threading;
using Cockpit.App.ViewModels;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.App.Plugins;

// The `IPluginSessionContext` handed to a header item: bound to one session for the life of its panel,
// unlike `PluginSessionObserver`, which tracks whichever session is selected. Events are marshalled to
// the UI thread; disposing detaches, so a closed session leaves no handler behind.
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
