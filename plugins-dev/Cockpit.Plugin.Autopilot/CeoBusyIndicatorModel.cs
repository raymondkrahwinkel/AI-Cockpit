using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.Plugin.Autopilot;

// Tracks whether the CEO planning session is mid-turn (AC-195) so the "Plan with the CEO" pop-out can show a
// "working" cue while a silent planning turn is in flight, instead of looking hung. Mirrors
// `IEmbeddedSession.IsBusy`; the shared global session indicator is left untouched so its deliberate deaf-during-streaming behaviour is not regressed app-wide.
internal sealed class CeoBusyIndicatorModel : IDisposable
{
    private readonly IEmbeddedSession _session;
    private readonly Action<bool> _onChanged;

    // Starts tracking `session` and invokes `onChanged` once with the current
    // busy state so the cue starts in sync, then again on every flip.
    public CeoBusyIndicatorModel(IEmbeddedSession session, Action<bool> onChanged)
    {
        _session = session;
        _onChanged = onChanged;
        IsWorking = session.IsBusy;
        session.BusyChanged += _OnBusyChanged;
        onChanged(IsWorking);
    }

    // Whether the CEO is mid-turn — the cue is shown exactly while this is true.
    public bool IsWorking { get; private set; }

    private void _OnBusyChanged(bool busy)
    {
        IsWorking = busy;
        _onChanged(busy);
    }

    public void Dispose() => _session.BusyChanged -= _OnBusyChanged;
}
