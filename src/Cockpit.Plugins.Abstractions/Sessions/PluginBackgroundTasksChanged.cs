namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// The set of work that has outlived its turn changed (AC-276) — sub-agents still running, shells still open.
/// </summary>
/// <remarks>
/// <see cref="Tasks"/> is the complete set as of this event, never a delta. A provider with no notion of such work
/// simply never raises this, which reads as "nothing outstanding", same as an empty list.
/// </remarks>
public sealed record PluginBackgroundTasksChanged : PluginSessionEvent
{
    /// <summary>
    /// Everything still outstanding right now. Empty when the last of it finished.
    /// </summary>
    public required IReadOnlyList<PluginBackgroundTask> Tasks { get; init; }
}
