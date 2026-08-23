namespace Cockpit.Plugins.Abstractions.Consent;

/// <summary>
/// The answer to a <see cref="ConsentRequest"/>: what the operator decided, and whether they chose to remember
/// it for the rest of the session (only ever possible for a <see cref="ConsentRisk.LowRisk"/> request — see
/// <see cref="ConsentRisk"/>).
/// </summary>
/// <param name="Outcome">
/// Approved or denied.
/// </param>
/// <param name="Remembered">
/// True when the operator asked not to be prompted again this session for this source and scope.
/// </param>
/// <param name="Bypassed">
/// True when nothing was ever shown — the operator switched the card off ahead of time for this source
/// (AC-575) — as opposed to an approval a card was actually raised and answered for. A caller that only reads
/// <see cref="IsApproved"/> cannot tell the two apart; this is what AC-759 needed the difference for, so a tool
/// result can report which one actually happened instead of a caller having to assume.
/// </param>
public sealed record ConsentDecision(ConsentOutcome Outcome, bool Remembered = false, bool Bypassed = false)
{
    /// <summary>
    /// Convenience for the fail-closed default: a plain denial.
    /// </summary>
    public static ConsentDecision Denied { get; } = new(ConsentOutcome.Denied);

    /// <summary>
    /// Whether the caller may go ahead.
    /// </summary>
    public bool IsApproved => Outcome == ConsentOutcome.Approved;
}
