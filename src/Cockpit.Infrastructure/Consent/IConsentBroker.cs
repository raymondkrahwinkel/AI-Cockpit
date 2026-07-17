using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.Infrastructure.Consent;

/// <summary>
/// The one host-side consent gate (#AC-47): plugins reach it through <c>ICockpitHost.RequestConsentAsync</c> and
/// host-internal callers (the terminal MCP, the orchestrator) resolve it directly, so the same Approve/Deny flow,
/// the same "remember for this session" rule, and the same audit trail serve all of them rather than each growing
/// its own. It lives in the host, never in a plugin — a plugin cannot approve its own action.
/// <para>
/// A request opens a prompt the UI shows (<see cref="PromptOpened"/>) and answers (<see cref="Respond"/>); the
/// awaiting call returns the decision. It fails closed: with no UI listening, or when the caller's token cancels,
/// the answer is <see cref="ConsentOutcome.Denied"/>, never a silent approval.
/// </para>
/// </summary>
public interface IConsentBroker
{
    /// <summary>
    /// Asks the operator to approve <paramref name="request"/> and waits for the answer. Returns immediately with
    /// an approval when the request is low-risk and the operator already chose to remember this source and scope
    /// this session; the dangerous class is always asked afresh. Denies without asking when no UI is listening or
    /// <paramref name="cancellationToken"/> fires.
    /// </summary>
    Task<ConsentDecision> RequestConsentAsync(ConsentRequest request, CancellationToken cancellationToken = default);

    /// <summary>Raised when a request needs the operator — the UI shows a prompt. Not raised for a remembered or fail-closed request, which resolves without asking.</summary>
    event EventHandler<ConsentPrompt>? PromptOpened;

    /// <summary>Raised when an opened prompt is resolved — by an answer, a cancellation, or the source going away — so the UI can take its surface down. Carries the prompt id.</summary>
    event EventHandler<Guid>? PromptClosed;

    /// <summary>The operator's answer to an open prompt, from the UI. <paramref name="remember"/> is honoured only for a rememberable prompt (<see cref="ConsentPrompt.CanRemember"/>). Unknown or already-resolved ids are ignored.</summary>
    void Respond(Guid promptId, ConsentOutcome outcome, bool remember);
}
