namespace Cockpit.App.ViewModels;

/// <summary>
/// Derives a coarse <see cref="SessionStatus"/> for a TTY session from its live JSONL transcript
/// activity, since a hosted TUI has no parsed event stream to read status from. Any appended transcript
/// line means a turn is in flight (<see cref="SessionStatus.Busy"/>); once the transcript falls quiet for
/// the idle threshold after having been active, the turn has finished and nothing is pending
/// (<see cref="SessionStatus.Done"/>); before the first line the session is simply waiting for the
/// operator to type (<see cref="SessionStatus.Idle"/>). Pure and clock-injected so the transitions are
/// unit-testable without a live pty. Permission/needs-action cannot be seen in the transcript, so this
/// never reports <see cref="SessionStatus.NeedsAttention"/> — that stays an SDK-only signal.
/// </summary>
public sealed class TtyActivityStatusTracker(TimeSpan idleThreshold)
{
    private DateTimeOffset? _lastActivity;

    /// <summary>Records a transcript line arriving at <paramref name="now"/> and returns the resulting status (always Busy — a line means a turn is producing output).</summary>
    public SessionStatus OnActivity(DateTimeOffset now)
    {
        _lastActivity = now;
        return SessionStatus.Busy;
    }

    /// <summary>Returns the status for <paramref name="now"/>: Idle before any activity, Busy while within the idle threshold of the last line, Done once the transcript has been quiet past it.</summary>
    public SessionStatus Poll(DateTimeOffset now)
    {
        if (_lastActivity is null)
        {
            return SessionStatus.Idle;
        }

        return now - _lastActivity.Value >= idleThreshold ? SessionStatus.Done : SessionStatus.Busy;
    }
}
