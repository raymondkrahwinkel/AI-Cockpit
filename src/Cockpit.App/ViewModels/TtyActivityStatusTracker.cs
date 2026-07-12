namespace Cockpit.App.ViewModels;

/// <summary>
/// Derives a coarse <see cref="SessionStatus"/> for a TTY session from its live JSONL transcript (#9), since a
/// hosted TUI has no parsed event stream to read status from. Fed the classification of each transcript line
/// (<see cref="TtyTranscriptStatus.ClassifyLine"/>): a turn-in-progress line makes it <see cref="SessionStatus.Busy"/>,
/// a turn-completed line makes it <see cref="SessionStatus.Done"/>, and before any line the session is simply
/// waiting for the operator (<see cref="SessionStatus.Idle"/>). Deliberately <em>not</em> a quiet-timeout: a
/// long <c>thinking</c> pause writes no line yet is very much busy, so status follows the last real signal, not
/// the clock. A generous safety timeout is the only clock left in — if a busy turn goes silent far past it
/// (an <c>end_turn</c> we somehow never saw, or a stalled/killed CLI), it falls back to Done rather than
/// showing a stuck spinner forever. Pure and clock-injected so the transitions are unit-testable without a live
/// pty. Permission/needs-action cannot be seen in the transcript, so this never reports
/// <see cref="SessionStatus.NeedsAttention"/> — that stays an SDK-only signal.
/// </summary>
public sealed class TtyActivityStatusTracker(TimeSpan busySafetyTimeout)
{
    private DateTimeOffset? _lastSignalAt;
    private bool _turnInProgress;
    private bool _seenAnySignal;

    /// <summary>
    /// Records a transcript line's classification at <paramref name="now"/> and returns the resulting status.
    /// <paramref name="turnInProgress"/> is <see langword="true"/> for a busy line, <see langword="false"/> for
    /// a turn-completed line, and <see langword="null"/> for a metadata line that leaves the status unchanged.
    /// </summary>
    public SessionStatus OnLine(bool? turnInProgress, DateTimeOffset now)
    {
        if (turnInProgress is { } inProgress)
        {
            _seenAnySignal = true;
            _turnInProgress = inProgress;
            _lastSignalAt = now;
        }

        return _Status(now);
    }

    /// <summary>Re-evaluates the status for <paramref name="now"/> without a new line — Idle before any signal, Done once a turn completed (or a busy turn went silent past the safety timeout), Busy otherwise.</summary>
    public SessionStatus Poll(DateTimeOffset now) => _Status(now);

    private SessionStatus _Status(DateTimeOffset now)
    {
        if (!_seenAnySignal)
        {
            return SessionStatus.Idle;
        }

        if (!_turnInProgress)
        {
            return SessionStatus.Done;
        }

        return _lastSignalAt is { } at && now - at >= busySafetyTimeout
            ? SessionStatus.Done
            : SessionStatus.Busy;
    }
}
