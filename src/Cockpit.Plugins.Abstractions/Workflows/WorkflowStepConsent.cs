namespace Cockpit.Plugins.Abstractions.Workflows;

/// <summary>
/// Whether a contributed workflow step needs the operator's consent to run, and at what risk (#AC-38). A non-trigger
/// step MUST declare this: a null (undeclared) <see cref="IWorkflowStep.RequiredConsent"/> is refused at load rather
/// than silently treated as safe, so a step that acts with the operator's rights can never slip through ungated.
/// Declare <see cref="None"/> for a genuinely safe step.
/// </summary>
public enum WorkflowStepConsent
{
    /// <summary>Safe to run without asking — a read, a transform, a local computation with no side effect worth gating.</summary>
    None,

    /// <summary>An idempotent, low-consequence action. Gated, and the operator may choose "remember for this session" (bound to the exact action).</summary>
    LowRisk,

    /// <summary>Acts with the operator's rights — a command, a session hand-off, arbitrary egress. Gated, asked afresh every time, never remembered.</summary>
    Dangerous,
}
