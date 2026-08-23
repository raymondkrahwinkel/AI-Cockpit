namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// The set of work that has outlived its turn changed (AC-276) — sub-agents still running, shells still open.
/// <para>
/// <see cref="Tasks"/> is the <em>complete</em> set as of this event, never a delta. That is deliberate and it is
/// what makes this safe to build a status on: a dropped or unparsed event costs nothing, because the next one
/// restates the whole truth. A start/stop ledger would instead have to be right every single time, and a single
/// missed end would strand a session on "working" forever — the failure mode this shape avoids by construction.
/// </para>
/// <para>
/// Emitted by a driver whose provider reports such work. A provider with no notion of it simply never raises this,
/// which reads as "nothing outstanding" — the same as an empty list, so no consumer needs a special case.
/// </para>
/// </summary>
public sealed record PluginBackgroundTasksChanged : PluginSessionEvent
{
    /// <summary>
    /// Everything still outstanding right now. Empty when the last of it finished.
    /// </summary>
    public required IReadOnlyList<PluginBackgroundTask> Tasks { get; init; }
}
