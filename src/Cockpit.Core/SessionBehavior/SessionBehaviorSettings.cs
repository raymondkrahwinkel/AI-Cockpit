namespace Cockpit.Core.SessionBehavior;

// User-configurable session-behaviour settings, persisted under the `sessionBehavior` section of
// `cockpit.json` (same store pattern as the profiles, notifications and transcript display). Holds
// whether typing "exit" closes the session once its turn completes (T10), and whether messages queued
// mid-turn are combined into a single follow-up turn (AC-145).
public sealed record SessionBehaviorSettings
{
    // When true, sending "exit" closes the session after that turn completes. Off by default.
    public bool AutoCloseOnExit { get; init; }

    // When true, all messages queued while a turn was in flight are sent together as one follow-up turn
    // once the turn completes, instead of one-per-turn (AC-145). Off by default — each queued message
    // keeps getting its own turn.
    public bool CombineQueuedMessages { get; init; }
}
