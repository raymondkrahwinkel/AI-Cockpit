using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.Infrastructure.Consent;

/// <summary>
/// A consent request waiting for the operator, handed to the UI so it can show an Approve/Deny surface (#AC-47).
/// The UI answers by calling <see cref="IConsentBroker.Respond"/> with this prompt's <see cref="Id"/>.
/// </summary>
/// <param name="Id">Identifies this pending prompt when the UI answers or when it is closed.</param>
/// <param name="Request">What is being asked — render <see cref="ConsentRequest.Action"/> verbatim.</param>
/// <param name="CanRemember">Whether to offer "remember for this session" — true only for a low-risk request that asked for it. The dangerous class is never rememberable.</param>
public sealed record ConsentPrompt(Guid Id, ConsentRequest Request, bool CanRemember);
