namespace Cockpit.Plugins.Abstractions.Consent;

/// <summary>
/// The answer to a <see cref="ConsentRequest"/>: what the operator decided, and whether they chose to remember
/// it for the rest of the session (only ever possible for a <see cref="ConsentRisk.LowRisk"/> request — see
/// <see cref="ConsentRisk"/>).
/// </summary>
/// <param name="Outcome">Approved or denied.</param>
/// <param name="Remembered">True when the operator asked not to be prompted again this session for this source and scope.</param>
public sealed record ConsentDecision(ConsentOutcome Outcome, bool Remembered = false)
{
    /// <summary>Convenience for the fail-closed default: a plain denial.</summary>
    public static ConsentDecision Denied { get; } = new(ConsentOutcome.Denied);

    /// <summary>Whether the caller may go ahead.</summary>
    public bool IsApproved => Outcome == ConsentOutcome.Approved;
}
