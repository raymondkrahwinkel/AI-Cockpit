namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// What kind of thing a usage signal measures (AC-229), which decides what the cockpit can offer to do about it.
/// </summary>
public enum PluginUsageSignalKind
{
    /// <summary>
    /// Fills up and can drain again, with no moment attached — a context window, emptied by a compaction rather
    /// than by the clock. There is nothing to schedule against, so a warning is all this kind ever produces.
    /// </summary>
    Fill,

    /// <summary>
    /// An allowance that spends down and rolls over at a moment the provider reports — a five-hour or weekly cap.
    /// Because it says when it returns, a session can be offered a resume timed to it.
    /// </summary>
    Allowance,
}
