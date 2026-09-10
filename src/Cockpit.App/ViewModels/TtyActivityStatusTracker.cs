using Cockpit.Core.Abstractions.Voice;

namespace Cockpit.App.ViewModels;

// TTY status (#9) follows provider-classified transcript activity, not quiet time: thinking can be silent but busy.
// A safety timeout clears a stalled CLI; AC-920 awaits-operator is NeedsAttention and exempt.
// AC-276 delays Done to avoid a false finish before late sub-agent status; turn_duration is not reliably present.
public sealed class TtyActivityStatusTracker(TimeSpan busySafetyTimeout, TimeSpan turnSettleDelay)
{
    // AC-1310: the ceiling on how long running processes may hold a silent turn off Done. Far above any slow tool
    // call, and deliberately short of SessionWatcher's 15-minute stuck threshold — a `tail -f` or a dev server is
    // silent forever, and pinning a pane on Busy would strand it behind a close confirmation for good (AC-276).
    private static readonly TimeSpan WorkingSafetyTimeout = TimeSpan.FromMinutes(10);

    private DateTimeOffset? _lastSignalAt;
    private SessionActivity _lastActivity = SessionActivity.None;
    private bool _seenAnySignal;
    private bool _processesPresent;
    private int _emptyProcessSamples;

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

    // AC-1310: what the resource sample that already reads the process table every two seconds saw under this
    // session. Asymmetric on purpose — seeing a process is proof and lands at once, while claiming silence takes
    // two consecutive empty samples, so a short `git` cannot flip the sidebar and back within one turn.
    public SessionStatus OnProcessSample(bool hasProcesses, DateTimeOffset now)
    {
        if (hasProcesses)
        {
            _processesPresent = true;
            _emptyProcessSamples = 0;
        }
        else if (++_emptyProcessSamples >= 2)
        {
            _processesPresent = false;
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
        // AC-1310: processes of its own lengthen that timeout, they never remove it. A turn waiting on a slow MCP
        // server is silent but not stalled; a session that never finishes still has to reach Done in the end.
        if (_lastSignalAt is { } at && now - at >= (_processesPresent ? WorkingSafetyTimeout : busySafetyTimeout))
        {
            return SessionStatus.Done;
        }

        return _lastActivity == SessionActivity.BackgroundBusy
            ? SessionStatus.WorkingBackground
            : SessionStatus.Busy;
    }
}
