namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// What kind of work a <see cref="PluginBackgroundTask"/> represents. The host weighs the two differently
/// (AC-276): a sub-agent is work the operator waits for, so it keeps the session off "done", while a shell may
/// well be a dev server or a <c>tail -f</c> that never ends — holding the status for that would be worse than
/// the premature "done" it set out to fix.
/// </summary>
public enum PluginBackgroundTaskKind
{
    /// <summary>
    /// The provider named a kind this build does not know. Deliberately ordinal 0, so an unmapped value — a newer
    /// CLI naming a task type this host has never heard of — lands on the least authoritative option rather than
    /// silently claiming to be a sub-agent and freezing the session's status on it.
    /// </summary>
    Unknown,

    /// <summary>
    /// A nested agent (the Task tool). Outstanding work the operator is waiting on.
    /// </summary>
    SubAgent,

    /// <summary>
    /// A backgrounded shell command that outlived the turn that started it.
    /// </summary>
    Shell,
}
