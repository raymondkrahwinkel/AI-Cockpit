using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.Infrastructure.Consent;

/// <summary>
/// The one host-side consent gate (#AC-47): plugins reach it via <c>ICockpitHost.RequestConsentAsync</c> and
/// host-internal callers resolve it directly, so Approve/Deny and the audit trail stay shared, never each growing
/// its own. Lives in the host, never a plugin; fails closed to <see cref="ConsentOutcome.Denied"/>, never silent approval.
/// </summary>
public interface IConsentBroker
{
    /// <summary>
    /// Asks the operator to approve <paramref name="request"/> and waits for the answer. Returns immediately with
    /// an approval when low-risk and already remembered for this source/session; the dangerous class is always
    /// asked afresh. Denies without asking when no UI is listening or <paramref name="cancellationToken"/> fires.
    /// </summary>
    Task<ConsentDecision> RequestConsentAsync(ConsentRequest request, CancellationToken cancellationToken = default);

    /// <summary>Raised when a request needs the operator — the UI shows a prompt. Not raised for a remembered or fail-closed request, which resolves without asking.</summary>
    event EventHandler<ConsentPrompt>? PromptOpened;

    /// <summary>Raised when an opened prompt is resolved — by an answer, a cancellation, or the source going away — so the UI can take its surface down. Carries the prompt id.</summary>
    event EventHandler<Guid>? PromptClosed;

    /// <summary>The operator's answer to an open prompt, from the UI. <paramref name="remember"/> is honoured only for a rememberable prompt (<see cref="ConsentPrompt.CanRemember"/>). Unknown or already-resolved ids are ignored.</summary>
    void Respond(Guid promptId, ConsentOutcome outcome, bool remember);
}
