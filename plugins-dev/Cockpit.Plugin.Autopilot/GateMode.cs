namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// Whether a done-gate is mandatory or advisory (decision #4). A hard gate stops the pipeline when it fails or its
/// capability is missing; a skippable gate is left out with a warning on the item rather than a stop.
/// </summary>
internal enum GateMode
{
    /// <summary>Must pass — a failure, or a missing capability, parks the run rather than shipping past it.</summary>
    Hard,

    /// <summary>Run it when it is available, but a miss is a warning on the item, not a stop.</summary>
    Skip,
}
