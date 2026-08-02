namespace Cockpit.Core.SessionBehavior;

/// <summary>
/// User-configurable session-behaviour settings, persisted under the <c>sessionBehavior</c> section of
/// <c>cockpit.json</c> (same store pattern as the profiles, notifications and transcript display). Holds
/// whether typing "exit" closes the session once its turn completes (T10), and whether messages queued
/// mid-turn are combined into a single follow-up turn (AC-145).
/// </summary>
public sealed record SessionBehaviorSettings
{
    /// <summary>When true, sending "exit" closes the session after that turn completes. Off by default.</summary>
    public bool AutoCloseOnExit { get; init; }

    /// <summary>
    /// When true, all messages queued while a turn was in flight are sent together as one follow-up turn
    /// once the turn completes, instead of one-per-turn (AC-145). Off by default — each queued message
    /// keeps getting its own turn.
    /// </summary>
    public bool CombineQueuedMessages { get; init; }

    /// <summary>
    /// Whether agent sessions may be woken by a neighbour's urgent message (AC-615) — a turn started for them, on
    /// their own desk, that the operator did not ask for. **On by default.**
    /// <para>
    /// This is where the consent for that lives. It used to be a per-session decision an agent made about itself
    /// through <c>set_wake_optin</c>, and the result was that nobody ever turned it on: an agent will not spend its
    /// operator's turn on its own say-so, and the operator never saw the choice because it was an MCP call rather
    /// than a setting. So the line was built and never used. Moving it here keeps the property that mattered — the
    /// person paying for the turn is the one who agreed to it — while making the default the useful one.
    /// </para>
    /// <para>
    /// A session can still override this for itself with <c>set_wake_optin</c>, in either direction, for as long as
    /// it lives. The rate limit on wakes (AC-396) is what makes an on-by-default setting safe to ship: it is the
    /// reason that cap had to be built first rather than last.
    /// </para>
    /// </summary>
    public bool WakeAgentsByDefault { get; init; } = true;
}
