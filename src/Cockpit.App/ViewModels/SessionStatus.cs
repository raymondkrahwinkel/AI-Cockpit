namespace Cockpit.App.ViewModels;

// Coarse-grained lifecycle/attention state for a single `SessionViewModel`,
// derived from the session events it already receives. Drives the sidebar status-dot and the
// "needs attention" affordance — see `Memory/Cockpit/Plan.md` §UX-eisen.
public enum SessionStatus
{
    // Not started, or started and waiting for the user to type — no turn in flight, nothing pending.
    Idle,

    // A turn is in flight (message sent, no `TurnCompleted`/error yet).
    Busy,

    // The main agent's own turn has gone quiet, but background work it spawned (a Claude sub-agent) is still running
    // (#9) — the session is not idle and closing it would interrupt real work, but the main agent is not itself
    // producing output.
    WorkingBackground,

    // The most recent turn finished successfully and nothing is pending.
    Done,

    // The most recent turn ended in a `SessionError` and no new turn has started since (AC-1309). Says something
    // about the agent, not about what is still running — a backgrounded shell survives this unaffected.
    Failed,

    // A tool-use permission decision is pending, or the CLI reported `needs_action` — the sidebar's "jumps out"
    // affordance (badge/highlight).
    NeedsAttention,
}
