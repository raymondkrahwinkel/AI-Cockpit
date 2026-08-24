namespace Cockpit.Core.Abstractions.Voice;

// AC-1013: A TTY session's coarse turn-activity as the generic transcript façade reports it to the host —
// the core's own mirror of the plugin's signal (core never references the plugin abstraction; the infra
// façade maps one to the other). Provider-neutral: plugin classifies, host maps to its status dot.
public enum SessionActivity
{
    // The line carries no turn-progress signal (metadata) — leave the status unchanged.
    None,

    // A turn is in flight: the main agent is producing output or looping into a tool call.
    Busy,

    // The main agent's own output is quiet but background work it spawned (a sub-agent) is still running — the session is not idle, and not the main agent actively working either.
    BackgroundBusy,

    // The turn finished (a terminal stop) and nothing is running — the session is done.
    TurnComplete,

    // The agent asked the operator a question it cannot proceed without an answer to — the session is blocked on a human, not on itself.
    AwaitingOperator,
}
