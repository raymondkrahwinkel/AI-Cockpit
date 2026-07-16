namespace Cockpit.Core.Abstractions.Voice;

/// <summary>
/// A TTY session's coarse turn-activity, as the generic transcript façade reports it to the host — the core
/// mirror of the plugin's own signal (the core does not reference the plugin abstraction, so the infra façade
/// maps one to the other). Provider-neutral: the plugin owns the format-specific classification, the host maps
/// these to its status dot.
/// </summary>
public enum SessionActivity
{
    /// <summary>The line carries no turn-progress signal (metadata) — leave the status unchanged.</summary>
    None,

    /// <summary>A turn is in flight: the main agent is producing output or looping into a tool call.</summary>
    Busy,

    /// <summary>The main agent's own output is quiet but background work it spawned (a sub-agent) is still running — the session is not idle, and not the main agent actively working either.</summary>
    BackgroundBusy,

    /// <summary>The turn finished (a terminal stop) and nothing is running — the session is done.</summary>
    TurnComplete,
}

/// <summary>
/// One activity reading from a session's transcript: the classified <see cref="Activity"/> plus the
/// <see cref="RawLine"/> it came from (null for a synthetic keep-alive), so the host drives both the status dot
/// and its raw-line observe surface from one tail.
/// </summary>
/// <param name="Activity">The classified turn-activity this reading represents.</param>
/// <param name="RawLine">The raw transcript line, or null for a synthetic signal (e.g. a background keep-alive).</param>
public sealed record SessionTranscriptActivity(SessionActivity Activity, string? RawLine);
