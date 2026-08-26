namespace Cockpit.Core.SessionBehavior;

// User-configurable session behaviour in `cockpit.json`'s `sessionBehavior` section: exit-after-turn (T10) and combined queued messages (AC-145).
public sealed record SessionBehaviorSettings
{
    // When true, sending "exit" closes the session after that turn completes. Off by default.
    public bool AutoCloseOnExit { get; init; }

    // When true, all messages queued while a turn was in flight are sent together as one follow-up turn
    // once the turn completes, instead of one-per-turn (AC-145). Off by default — each queued message
    // keeps getting its own turn.
    public bool CombineQueuedMessages { get; init; }

    // Neighbor wake-ups default on (AC-615), with consent in the operator-visible setting rather than an agent MCP choice.
    // Sessions may override it temporarily; AC-396's wake rate limit makes that useful default safe.
    // The old per-session opt-in stayed unused because the operator never saw it and an agent cannot consent for them.
    public bool WakeAgentsByDefault { get; init; } = true;
}
