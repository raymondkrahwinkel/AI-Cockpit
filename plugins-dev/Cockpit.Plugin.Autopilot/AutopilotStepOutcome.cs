namespace Cockpit.Plugin.Autopilot;

// What a single step execution actually produced (AC-347): whether a verdict was ever reached, and if so, what it
// was — the discriminator a plain `bool` could not carry, since `Rejected` and `Faulted` both used to collapse
// into "false". Passed through `executeStep`'s return value only, never persisted.
internal enum AutopilotStepOutcome
{
    // The CEO validated the step's output against its acceptance and accepted it.
    Passed,

    // The CEO validated the step's output against its acceptance and turned it down — a genuine review finding.
    Rejected,

    // No verdict was ever reached: a crashed or refused session, a stalled agent, a dead CEO, a profile/model
    // mismatch, or a cancelled run. The step still counts as a failed attempt for the rework loop, but it is never a
    // review finding — nobody judged the work.
    Faulted,
}
