using Avalonia.Controls;
using Avalonia.Threading;
using Cockpit.Plugin.LocalCi.Execution;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.LocalCi.Ui;

// The last local run in this session's checkout, in this session's header. A strip has room for an indicator and
// not for a panel, so it is one short line with the whole story on its tooltip — and nothing at all until a run
// has happened, which is most sessions most of the time.
// Deliberately not the session's statusline: that line carries the ticket the session is working on, and a build
// result written over it trades a fact that lasts all day for one that lasts until the next run.
internal sealed class LocalCiSessionBadge : UserControl
{
    private readonly IPluginSessionContext _session;
    private readonly LocalRunTracker _tracker;
    private readonly ICockpitSessionObserver _sessions;
    private readonly TextBlock _label = new();

    public LocalCiSessionBadge(IPluginSessionContext session, LocalRunTracker tracker, ICockpitSessionObserver sessions)
    {
        _session = session;
        _tracker = tracker;
        _sessions = sessions;

        Content = _label;
        IsVisible = false;

        tracker.Changed += _OnChanged;
        session.WorkingDirectoryChanged += _OnWorkingDirectoryChanged;
        // The pane closing — not DetachedFromVisualTree — is the reliable teardown cue. A closed session's visuals
        // are held by the composition tree and never detach, so the old detach-only unsubscribe leaked: the badge
        // stayed on the plugin-lived tracker's Changed list, keeping the session (and its whole transcript) alive.
        // Unsubscribe on either signal; both are idempotent.
        sessions.SessionClosed += _OnSessionClosed;
        DetachedFromVisualTree += (_, _) => _Unsubscribe();

        _Show();
    }

    private void _OnWorkingDirectoryChanged(object? sender, EventArgs e) => _Show();

    private void _OnSessionClosed(object? sender, string paneId)
    {
        if (string.Equals(paneId, _session.PaneId, StringComparison.Ordinal))
        {
            _Unsubscribe();
        }
    }

    private void _Unsubscribe()
    {
        _tracker.Changed -= _OnChanged;
        _session.WorkingDirectoryChanged -= _OnWorkingDirectoryChanged;
        _sessions.SessionClosed -= _OnSessionClosed;
    }

    private void _OnChanged() => Dispatcher.UIThread.Post(_Show);

    private void _Show()
    {
        if (_session.WorkingDirectory is not { Length: > 0 } checkout || _tracker.LastFor(checkout) is not { } last)
        {
            IsVisible = false;
            return;
        }

        IsVisible = true;
        _label.Text = last.Result.Outcome switch
        {
            LocalRunOutcome.Passed => $"local: {last.Result.JobId} ✓",
            LocalRunOutcome.Failed => $"local: {last.Result.JobId} ✗",
            _ => $"local: {last.Result.JobId} —",
        };

        ToolTip.SetTip(_label, last.Result.Headline);
    }
}
