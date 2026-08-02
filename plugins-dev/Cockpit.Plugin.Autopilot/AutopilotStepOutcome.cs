namespace Cockpit.Plugin.Autopilot;

// What a single step execution actually produced (AC-347): whether a verdict was ever reached, and if so, what it
// was. This is the discriminator a plain `bool` could not carry — `Rejected` and `Faulted`
// both used to collapse into "false", so a rework the CEO's validation actually sent back was indistinguishable from a
// step that crashed, stalled, or never got a session at all. Passed through `executeStep`'s return value only —
// never persisted, so it carries no note about enum ordering the way a stored one would.
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
