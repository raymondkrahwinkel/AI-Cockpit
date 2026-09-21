namespace Cockpit.Plugin.Autopilot;

// How an epic run's sub gets onto its collection branch once both review gates passed (AC-1338, D1). Set per run;
// the one named default is `AutopilotSettings.DefaultMergeMode`.
internal enum AutopilotMergeMode
{
    // Autopilot stops at the gate and waits for a go from the operator or the assistant before anything is merged —
    // EpicWorkflow §3 followed one-to-one.
    Explicit,

    // Autopilot merges as soon as both gates passed and the branch builds, without waiting for anyone. Deliberately
    // not how the manual workflow works; D1 makes it a per-run choice because not everyone works the same way.
    Automatic,
}
