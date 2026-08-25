namespace Cockpit.Plugin.Autopilot;

// What kind of correction a settled step needed (AC-347). `None` is the default (0) so pre-existing
// history reads back as "no correction". Persisted as the plain integer (no `JsonStringEnumConverter`),
// so a new value must be added last, never inserted between existing ones, or old history silently reclassifies.
internal enum AutopilotCorrectionKind
{
    // The step ran clean — no rework, no restart, no manual edit.
    None = 0,

    // A gate sent the step back and the code changed before the merge — a review finding, human or agent.
    ReviewFinding,

    // The step ran out of attempts, or the run had to be picked up again.
    RunRestart,

    // A human changed the work before merging it — never detected automatically, only ever set by hand.
    OperatorEdit,
}
