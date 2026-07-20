namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// The done-gates a run passes before it is "ready" (decision #4). The gates themselves land in a later Autopilot
/// sub-ticket; this names them so their per-gate hard/skip setting has something to key on.
/// </summary>
internal enum GateKind
{
    /// <summary>Visual verify (AC-86).</summary>
    Verify,

    /// <summary>Code review (/code-review).</summary>
    CodeReview,

    /// <summary>Security review (/security-review). Hard by default.</summary>
    Security,

    /// <summary>Language + memory conventions check.</summary>
    Conventions,
}
