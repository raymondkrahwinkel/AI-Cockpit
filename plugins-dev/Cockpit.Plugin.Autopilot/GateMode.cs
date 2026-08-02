namespace Cockpit.Plugin.Autopilot;

// Whether a done-gate is mandatory or advisory (decision #4). A hard gate stops the pipeline when it fails or its
// capability is missing; a skippable gate is left out with a warning on the item rather than a stop.
internal enum GateMode
{
    // Must pass — a failure, or a missing capability, parks the run rather than shipping past it.
    Hard,

    // Run it when it is available, but a miss is a warning on the item, not a stop.
    Skip,
}
