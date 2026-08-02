namespace Cockpit.Core.Usage;

// What kind of work ran up a session's usage (AC-251), so a month of records can be read apart.
public enum UsageRunKind
{
    // A session the operator opened and drives themselves.
    Interactive,

    // A session a plugin embedded to do its own work — an Autopilot step, its planning CEO — rather than one the operator types into.
    Embedded,
}
