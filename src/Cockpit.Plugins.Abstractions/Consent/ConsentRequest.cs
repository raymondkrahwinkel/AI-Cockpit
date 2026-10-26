namespace Cockpit.Plugins.Abstractions.Consent;

/// <summary>
/// Asks the operator to approve a single action before it happens — the one consent primitive shared by
/// anything that acts with the operator's rights on an agent's say-so. Passed to
/// <c>ICockpitHost.RequestConsentAsync</c>, which shows an Approve/Deny surface and returns the
/// <see cref="ConsentDecision"/>.
/// </summary>
/// <remarks>
/// <see cref="Action"/> is the ground truth and is rendered verbatim: it must be the literal thing that will
/// run, never a caller-composed summary — a prompt-injected agent controls the words it supplies, so a friendly
/// description of a hostile command would be a gate that approves it.
/// </remarks>
/// <param name="Title">
/// A short line naming what is being asked — "Workflow wants to run a command". Host-side framing, distinct from the untrusted <paramref name="Action"/>.
/// </param>
/// <param name="Action">
/// The literal action, shown verbatim and read-only: the real command + working directory, the real URL, the pane. Never a summary.
/// </param>
/// <param name="Source">
/// Who is asking (session, plugin) — see <see cref="ConsentSource"/>.
/// </param>
/// <param name="Scope">
/// A stable key for "remember this", identifying the kind of action (e.g. <c>workflow.http:GET</c>). Only consulted for a <see cref="ConsentRisk.LowRisk"/> request.
/// </param>
/// <param name="Risk">
/// Whether this is a <see cref="ConsentRisk.Dangerous"/> action (never remembered) or <see cref="ConsentRisk.LowRisk"/>.
/// </param>
/// <param name="AllowRemember">
/// Whether to offer "remember for this session". Honoured only when <paramref name="Risk"/> is <see cref="ConsentRisk.LowRisk"/>; ignored for a dangerous action.
/// </param>
/// <param name="PreApprovedBy">
/// Set when the calling plugin's own policy already decided this request — a daemon or cluster consent mode the
/// operator chose ahead of time, named here (e.g. <c>"daemon mode: read-free"</c>). When set, no prompt is shown;
/// the decision comes back <see cref="ConsentDecision.Bypassed"/> instead. A plugin gains nothing over simply not
/// asking — this only makes that skip visible in the audit trail (AC-1347/AC-1348).
/// </param>
public sealed record ConsentRequest(
    string Title,
    string Action,
    ConsentSource Source,
    string Scope,
    ConsentRisk Risk,
    bool AllowRemember = false,
    string? PreApprovedBy = null)
{
    /// <summary>
    /// Questions the operator answers on the card by picking (AC-1714), returned in <see cref="ConsentDecision.Chosen"/>.
    /// A request with choices is always shown: never remembered, never bypassed, and denied when
    /// <see cref="PreApprovedBy"/> is set, since only the operator can pick. A plugin that sets it needs minHostVersion 0.117.0.
    /// </summary>
    public IReadOnlyList<ConsentChoice>? Choices { get; init; }

    /// <summary>
    /// The approve button's text on a card with choices, naming what approving does ("Share watching"); "Approve" when unset.
    /// </summary>
    public string? ApproveLabel { get; init; }

    /// <summary>
    /// What this approval never covers, whatever is picked, shown as its own warning block on a card with choices.
    /// </summary>
    public IReadOnlyList<string>? Never { get; init; }

    /// <summary>
    /// On a card with choices that names a session: the card stands under that session's conversation instead of
    /// over it, as buttons: the approve button (<see cref="ApproveLabel"/>) with what is picked, a button
    /// (<see cref="OtherLabel"/>) that lists the other options to approve with, and No. A host whose view has no such place
    /// shows it as the overlay card (AC-1715). Needs minHostVersion 0.121.0.
    /// </summary>
    public bool Inline { get; init; }

    /// <summary>
    /// The button of an <see cref="Inline"/> card that lists the other options, "Other tab..."; the options stay a
    /// list on the approve button's card when unset.
    /// </summary>
    public string? OtherLabel { get; init; }
}
