namespace Cockpit.Core.Usage;

/// <summary>What kind of work ran up a session's usage (AC-251), so a month of records can be read apart.</summary>
public enum UsageRunKind
{
    /// <summary>A session the operator opened and drives themselves.</summary>
    Interactive,

    /// <summary>A session a plugin embedded to do its own work — an Autopilot step, its planning CEO — rather than one the operator types into.</summary>
    Embedded,
}
