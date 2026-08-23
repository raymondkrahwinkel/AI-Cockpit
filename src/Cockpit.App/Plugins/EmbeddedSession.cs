using System.ComponentModel;
using Avalonia.Controls;
using Cockpit.App.ViewModels;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.App.Plugins;

// The host's `IEmbeddedSession`: view, pane id, end-task, composer toggle and end callback.
// The host owns the session (nothing here to dispose). AC-195: also forwards turn-busy
// state so an embedder (e.g. the Autopilot plan pop-out's CEO) can show a "working" cue.
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
        session.ToolActivity += _OnToolActivity;
    }

    public Control View { get; }

    public string PaneId => _session.PaneId;

    public Task<string?> Completion { get; }

    // AC-1037: the session's own working directory — the isolated worktree the host resolved for it, when it made
    // one. Read live rather than captured at construction: this adapter is handed back before the start has resolved
    // the directory, so a captured value would be null for the whole session.
    public string? WorktreePath => _session.WorkingDirectory;

    public bool IsBusy => _isBusy;

    public event Action<bool>? BusyChanged;

    public event Action? Activity;

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

    // The session raises ToolActivity on the UI thread, so the forwarded Activity is already marshalled for an embedder
    // that touches its controls. A pure pass-through: the embedder decides what progress means (Autopilot resets its
    // per-step stall deadline on it).
    private void _OnToolActivity() => Activity?.Invoke();
}
