using Cockpit.App.Services;
using Cockpit.Core.Abstractions;
using Cockpit.Infrastructure.Consent;
using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.App.Composition;

// AC-1441: the desktop's consent prompts, passed straight through to the in-proc broker.
internal sealed class BrokerConsentPrompts : IConsentPrompts, ISingletonService
{
    private readonly IConsentBroker _broker;

    public BrokerConsentPrompts(IConsentBroker broker)
    {
        _broker = broker;
        broker.PromptOpened += (_, prompt) => Opened?.Invoke(this, new ConsentQuestion(prompt.Id, prompt.Request, prompt.CanRemember));
        broker.PromptClosed += (_, promptId) => Closed?.Invoke(this, promptId);
    }

    public event EventHandler<ConsentQuestion>? Opened;

    public event EventHandler<Guid>? Closed;

    public Task<ConsentDecision> RequestAsync(ConsentRequest request, CancellationToken cancellationToken = default) =>
        _broker.RequestConsentAsync(request, cancellationToken);

    public void Respond(Guid promptId, ConsentOutcome outcome, bool remember) => _broker.Respond(promptId, outcome, remember);
}
