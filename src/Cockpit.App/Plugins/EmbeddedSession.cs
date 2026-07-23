using System.ComponentModel;
using Avalonia.Controls;
using Cockpit.App.ViewModels;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.App.Plugins;

/// <summary>
/// The host's <see cref="IEmbeddedSession"/>: the session view to place, the pane id to act on it, the task that
/// completes when the session ends, the callback that toggles its composer, and the host callback that ends this one
/// session. The host owns the session, so there is nothing here to dispose.
/// <para>
/// It also forwards the session's turn-busy state (AC-195): <see cref="IsBusy"/> mirrors
/// <see cref="SessionViewModel.IsBusy"/> and <see cref="BusyChanged"/> fires as a turn starts and settles, so an
/// embedder that shows the session — the Autopilot plan pop-out's CEO — can light a "working" cue while a long,
/// silent planning turn runs instead of looking hung. The busy source is the live session view model; the adapter
/// lives as long as the embedder holds it, which is within the session's own lifetime.
/// </para>
/// </summary>
internal sealed class EmbeddedSession : IEmbeddedSession
{
    private readonly SessionViewModel _session;
    private readonly Action<bool> _setInput;
    private readonly Func<Task> _close;
    private bool _isBusy;

    public EmbeddedSession(Control view, SessionViewModel session, Task<string?> completion, Action<bool> setInput, Func<Task> close)
    {
        View = view;
        _session = session;
        Completion = completion;
        _setInput = setInput;
        _close = close;
        _isBusy = session.IsBusy;
        session.PropertyChanged += _OnSessionPropertyChanged;
    }

    public Control View { get; }

    public string PaneId => _session.PaneId;

    public Task<string?> Completion { get; }

    public bool IsBusy => _isBusy;

    public event Action<bool>? BusyChanged;

    public void SetInputEnabled(bool enabled) => _setInput(enabled);

    public Task CloseAsync() => _close();

    // The session raises PropertyChanged on the UI thread, so the forwarded BusyChanged is already marshalled for an
    // embedder that touches its controls. Guard on the actual transition so a turn that touches IsBusy without
    // changing it does not fan out a redundant event.
    private void _OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SessionViewModel.IsBusy) || _session.IsBusy == _isBusy)
        {
            return;
        }

        _isBusy = _session.IsBusy;
        BusyChanged?.Invoke(_isBusy);
    }
}
