using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.App.Services;

/// <summary>
/// The consent prompts the desktop shows and answers (AC-47), over the backend's consent broker (AC-1441).
/// Local to the desktop: a remote frontend needs them over the line first (AC-1388).
/// </summary>
public interface IConsentPrompts
{
    /// <summary>
    /// Raised when a prompt opens and waits for an answer.
    /// </summary>
    event EventHandler<ConsentQuestion>? Opened;

    /// <summary>
    /// Raised with a prompt's id once it is answered, wherever that happened.
    /// </summary>
    event EventHandler<Guid>? Closed;

    /// <summary>
    /// Asks for consent and waits for the answer.
    /// </summary>
    Task<ConsentDecision> RequestAsync(ConsentRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Answers the prompt <paramref name="promptId"/>; <paramref name="remember"/> counts only where the prompt allows it.
    /// </summary>
    void Respond(Guid promptId, ConsentOutcome outcome, bool remember);
}

// One open prompt as the desktop shows it: the request verbatim, and whether "remember" may be offered.
public sealed record ConsentQuestion(Guid Id, ConsentRequest Request, bool CanRemember);
