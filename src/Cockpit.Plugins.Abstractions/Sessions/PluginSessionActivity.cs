namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// A TTY session's coarse turn-activity, classified by the provider plugin from its own transcript (the host has
/// no parsed event stream for a hosted TUI). Provider-neutral on purpose — the plugin owns the format-specific
/// reading, the host only maps these signals to its status dot.
/// </summary>
public enum PluginSessionActivity
{
    /// <summary>The line carries no turn-progress signal (metadata) — leave the status unchanged.</summary>
    None,

    /// <summary>A turn is in flight: the main agent is producing output or looping into a tool call.</summary>
    Busy,

    /// <summary>
    /// The main agent's own output has gone quiet, but background work it spawned (a sub-agent) is still running —
    /// the session is not idle, but not the main agent actively typing either. Emitted as a keep-alive so a long
    /// background run never reads as "done".
    /// </summary>
    BackgroundBusy,

    /// <summary>The turn finished (a terminal stop) and nothing is running — the session is done.</summary>
    TurnComplete,
}
