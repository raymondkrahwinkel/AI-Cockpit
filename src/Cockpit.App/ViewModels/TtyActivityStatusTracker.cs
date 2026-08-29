using Cockpit.Core.Abstractions.Voice;

namespace Cockpit.App.ViewModels;

// TTY status (#9) follows provider-classified transcript activity, not quiet time: thinking can be silent but busy.
// A safety timeout clears a stalled CLI; AC-920 awaits-operator is NeedsAttention and exempt.
// AC-276 delays Done to avoid a false finish before late sub-agent status; turn_duration is not reliably present.
public sealed class TtyActivityStatusTracker(TimeSpan busySafetyTimeout, TimeSpan turnSettleDelay)
{
    private DateTimeOffset? _lastSignalAt;
    private SessionActivity _lastActivity = SessionActivity.None;
    private bool _seenAnySignal;

    // Records a transcript reading's classified activity at `now` and returns the resulting
    // status. `SessionActivity.None` is a metadata reading that leaves the status unchanged.
    public SessionStatus OnActivity(SessionActivity activity, DateTimeOffset now)
    {
        if (activity != SessionActivity.None)
        {
            _seenAnySignal = true;
            _lastActivity = activity;
            _lastSignalAt = now;
        }

        return _Status(now);
    }

    // Re-evaluates the status for `now` without a new reading — Idle before any signal, Done once a turn completed (or a busy turn went silent past the safety timeout), else Busy/Working-background per the last signal.
    public SessionStatus Poll(DateTimeOffset now) => _Status(now);

    // AC-75 refreshes a busy turn's timeout on visible output without inventing or reviving completed/idle turns.
    // Output after a timed-out busy turn restores Busy; a stalled CLI stays silent and still reaches Done.
    public SessionStatus OnAlive(DateTimeOffset now)
    {
        if (_seenAnySignal && _lastActivity is SessionActivity.Busy or SessionActivity.BackgroundBusy)
        {
            _lastSignalAt = now;
        }

        return _Status(now);
    }

    private SessionStatus _Status(DateTimeOffset now)
    {
        if (!_seenAnySignal)
        {
            return SessionStatus.Idle;
        }

        if (_lastActivity == SessionActivity.TurnComplete)
        {
            // Hold the finish briefly: the count of still-running sub-agents arrives on a separate line just after
            // this one, and reporting Done in between is what makes the pill flicker and the notification fire
            // early. Once the delay has passed with no such correction, the turn really is over.
            return _lastSignalAt is { } completedAt && now - completedAt < turnSettleDelay
                ? SessionStatus.Busy
                : SessionStatus.Done;
        }

        // AC-920: the operator, not the model, owes the next move — exempt from the safety timeout, since a
        // prompt that sits unanswered for ten minutes is still a prompt, not a stalled CLI.
        if (_lastActivity == SessionActivity.AwaitingOperator)
        {
            return SessionStatus.NeedsAttention;
        }

        // Busy or BackgroundBusy — but a turn that went silent far past the safety timeout falls back to Done.
        if (_lastSignalAt is { } at && now - at >= busySafetyTimeout)
        {
            return SessionStatus.Done;
        }

        return _lastActivity == SessionActivity.BackgroundBusy
            ? SessionStatus.WorkingBackground
            : SessionStatus.Busy;
    }
}
