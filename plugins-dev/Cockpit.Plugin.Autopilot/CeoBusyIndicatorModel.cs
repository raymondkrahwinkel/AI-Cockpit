using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// Tracks whether the CEO planning session is mid-turn (AC-195) so the "Plan with the CEO" pop-out can show a
/// "working" cue while a planning turn — which can run silently for minutes — is in flight, instead of leaving the
/// dialog looking hung. It mirrors <see cref="IEmbeddedSession.IsBusy"/> and follows
/// <see cref="IEmbeddedSession.BusyChanged"/>; the state rule is separated from the rendering so it is unit-testable
/// without a view. The CEO-only cue lives here on purpose — the shared global session indicator is left untouched so
/// its deliberate deaf-during-streaming behaviour is not regressed app-wide.
/// </summary>
internal sealed class CeoBusyIndicatorModel : IDisposable
{
    private readonly IEmbeddedSession _session;
    private readonly Action<bool> _onChanged;

    /// <summary>
    /// Starts tracking <paramref name="session"/> and invokes <paramref name="onChanged"/> once with the current
    /// busy state so the cue starts in sync, then again on every flip.
    /// </summary>
    public CeoBusyIndicatorModel(IEmbeddedSession session, Action<bool> onChanged)
    {
        _session = session;
        _onChanged = onChanged;
        IsWorking = session.IsBusy;
        session.BusyChanged += _OnBusyChanged;
        onChanged(IsWorking);
    }

    /// <summary>Whether the CEO is mid-turn — the cue is shown exactly while this is true.</summary>
    public bool IsWorking { get; private set; }

    private void _OnBusyChanged(bool busy)
    {
        IsWorking = busy;
        _onChanged(busy);
    }

    public void Dispose() => _session.BusyChanged -= _OnBusyChanged;
}
