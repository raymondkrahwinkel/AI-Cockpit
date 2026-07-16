using Cockpit.Core.Abstractions.Voice;

namespace Cockpit.App.ViewModels;

/// <summary>
/// Derives a coarse <see cref="SessionStatus"/> for a TTY session from its live transcript activity (#9), since a
/// hosted TUI has no parsed event stream to read status from. Fed a provider-classified <see cref="SessionActivity"/>
/// per reading (the provider plugin owns the format-specific classification): a <see cref="SessionActivity.Busy"/>
/// reading makes it <see cref="SessionStatus.Busy"/>, a <see cref="SessionActivity.BackgroundBusy"/> reading (the
/// main agent quiet while a sub-agent still runs) makes it <see cref="SessionStatus.WorkingBackground"/>, a
/// <see cref="SessionActivity.TurnComplete"/> reading makes it <see cref="SessionStatus.Done"/>, and before any
/// signal the session is simply waiting for the operator (<see cref="SessionStatus.Idle"/>). Deliberately
/// <em>not</em> a quiet-timeout: a long <c>thinking</c> pause writes no line yet is very much busy, so status
/// follows the last real signal, not the clock. A generous safety timeout is the only clock left in — if a busy
/// turn goes silent far past it (an end-of-turn we somehow never saw, or a stalled/killed CLI), it falls back to
/// Done rather than showing a stuck spinner forever; a live sub-agent keeps emitting BackgroundBusy keep-alives,
/// so a long background run never trips that timeout. Pure and clock-injected so the transitions are unit-testable
/// without a live pty. Permission/needs-action cannot be seen in the transcript, so this never reports
/// <see cref="SessionStatus.NeedsAttention"/> — that stays an SDK-only signal.
/// </summary>
public sealed class TtyActivityStatusTracker(TimeSpan busySafetyTimeout)
{
    private DateTimeOffset? _lastSignalAt;
    private SessionActivity _lastActivity = SessionActivity.None;
    private bool _seenAnySignal;

    /// <summary>
    /// Records a transcript reading's classified activity at <paramref name="now"/> and returns the resulting
    /// status. <see cref="SessionActivity.None"/> is a metadata reading that leaves the status unchanged.
    /// </summary>
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

    /// <summary>Re-evaluates the status for <paramref name="now"/> without a new reading — Idle before any signal, Done once a turn completed (or a busy turn went silent past the safety timeout), else Busy/Working-background per the last signal.</summary>
    public SessionStatus Poll(DateTimeOffset now) => _Status(now);

    private SessionStatus _Status(DateTimeOffset now)
    {
        if (!_seenAnySignal)
        {
            return SessionStatus.Idle;
        }

        if (_lastActivity == SessionActivity.TurnComplete)
        {
            return SessionStatus.Done;
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
