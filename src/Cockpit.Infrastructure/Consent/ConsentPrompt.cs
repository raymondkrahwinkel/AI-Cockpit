using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.Infrastructure.Consent;

// A consent request waiting for the operator, handed to the UI so it can show an Approve/Deny surface (#AC-47).
// The UI answers by calling `IConsentBroker.Respond` with this prompt's `Id`. `CanRemember` offers
// "remember for this session" only for a low-risk request that asked for it — the dangerous class is never rememberable.
public sealed record ConsentPrompt(Guid Id, ConsentRequest Request, bool CanRemember);
