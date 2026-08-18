using Cockpit.Core.Abstractions.Voice;

namespace Cockpit.App.ViewModels;

// Derives a coarse `SessionStatus` for a TTY session from its live transcript activity (#9), since a
// hosted TUI has no parsed event stream to read status from. Fed a provider-classified `SessionActivity`
// per reading (the provider plugin owns the format-specific classification): a `SessionActivity.Busy`
// reading makes it `SessionStatus.Busy`, a `SessionActivity.BackgroundBusy` reading (the
// main agent quiet while a sub-agent still runs) makes it `SessionStatus.WorkingBackground`, a
// `SessionActivity.TurnComplete` reading makes it `SessionStatus.Done`, and before any
// signal the session is simply waiting for the operator (`SessionStatus.Idle`). Deliberately
// *not* a quiet-timeout: a long `thinking` pause writes no line yet is very much busy, so status
// follows the last real signal, not the clock. A generous safety timeout is the only clock left in — if a busy
// turn goes silent far past it (an end-of-turn we somehow never saw, or a stalled/killed CLI), it falls back to
// Done rather than showing a stuck spinner forever; a live sub-agent keeps emitting BackgroundBusy keep-alives,
// so a long background run never trips that timeout. Pure and clock-injected so the transitions are unit-testable
// without a live pty. A general tool-use permission cannot be told apart from a running tool in the transcript,
// but `SessionActivity.AwaitingOperator` (AC-920: an `AskUserQuestion` call, which has no non-interactive path)
// can, and reads as `SessionStatus.NeedsAttention`, exempt from the safety timeout.
//
// `busySafetyTimeout`: How long a busy turn may go completely silent before falling back to Done.
// `turnSettleDelay`:
// How long a completed turn is held at Busy before it is allowed to read Done (AC-276). On this route the
// assistant line that ends a turn arrives before the CLI states how many sub-agents are still running: measured
// across 232 transcripts, the turn_duration line carrying that count follows the end_turn line by a median of
// 1101 ms (p99 2634 ms). Emitting Done in that gap and correcting to WorkingBackground a second later is exactly
// the flicker this ticket is about — and worse, it fires the "session finished" notification on the way through.
// Waiting it out costs a turn ending shown up to this late; announcing a session that is still working costs the
// operator's trust in the notification, which is the more expensive of the two.
//
// This cannot instead be solved by treating turn_duration itself as the end of the turn: only 2481 of 4293
// turn-ending assistant lines are followed by one, so 42% of turns would never settle.
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

    // Records that the session is still visibly alive at `now` — its TUI produced output, e.g. a
    // thinking spinner ticking or text streaming (AC-75) — without changing what it is doing. While a turn is busy
    // this refreshes the safety-timeout clock, so a long but visibly-working silent turn never decays to a false
    // Done. A turn that genuinely completed (`SessionActivity.TurnComplete` → Done) or one not yet
    // started (Idle) is left alone: a liveness signal never invents a turn or revives a finished one. A busy turn
    // that had *decayed* to Done via the safety timeout, though, is not finished — its last activity is
    // still Busy — so renewed output recovers it to Busy, which is what an alive session should read. A truly
    // stalled or killed CLI produces no output at all, so its turn still times out to Done — the safety net is
    // unchanged.
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
