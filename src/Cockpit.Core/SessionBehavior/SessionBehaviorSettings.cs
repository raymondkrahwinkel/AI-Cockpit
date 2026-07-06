namespace Cockpit.Core.SessionBehavior;

/// <summary>
/// User-configurable session-behaviour settings, persisted under the <c>sessionBehavior</c> section of
/// <c>cockpit.json</c> (same store pattern as the profiles, notifications and transcript display). Holds
/// whether typing "exit" closes the session once its turn completes (T10).
/// </summary>
public sealed record SessionBehaviorSettings
{
    /// <summary>When true, sending "exit" closes the session after that turn completes. Off by default.</summary>
    public bool AutoCloseOnExit { get; init; }
}
