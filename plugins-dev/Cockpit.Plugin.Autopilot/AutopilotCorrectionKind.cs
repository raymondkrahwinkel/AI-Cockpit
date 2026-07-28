namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// What kind of correction a settled step needed (AC-347) — the classification the reliability count is built on.
/// <see cref="None"/> is the default (0) so persisted history from before this field existed reads back as "no
/// correction" rather than some other value.
/// </summary>
internal enum AutopilotCorrectionKind
{
    /// <summary>The step ran clean — no rework, no restart, no manual edit.</summary>
    None = 0,

    /// <summary>A gate sent the step back and the code changed before the merge — a review finding, human or agent.</summary>
    ReviewFinding,

    /// <summary>The step ran out of attempts, or the run had to be picked up again.</summary>
    RunRestart,

    /// <summary>A human changed the work before merging it — never detected automatically, only ever set by hand.</summary>
    OperatorEdit,
}
