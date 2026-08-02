using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.Infrastructure.Consent;

// A consent request waiting for the operator, handed to the UI so it can show an Approve/Deny surface (#AC-47).
// The UI answers by calling `IConsentBroker.Respond` with this prompt's `Id`.
//
// `Id`: Identifies this pending prompt when the UI answers or when it is closed.
// `Request`: What is being asked — render `ConsentRequest.Action` verbatim.
// `CanRemember`: Whether to offer "remember for this session" — true only for a low-risk request that asked for it. The dangerous class is never rememberable.
public sealed record ConsentPrompt(Guid Id, ConsentRequest Request, bool CanRemember);
