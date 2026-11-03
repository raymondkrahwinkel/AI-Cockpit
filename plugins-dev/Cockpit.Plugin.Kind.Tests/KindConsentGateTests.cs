using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;
using Cockpit.Plugin.Kind.Security;
using NSubstitute;

namespace Cockpit.Plugin.Kind.Tests;

// AC-179 criterion 7: kind create/delete has no cluster registration to authorize against, so it is always
// Dangerous, never remembered, and a create approval must not double as a delete approval.
public class KindConsentGateTests
{
    private const string PaneId = "pane-1";

    [Fact]
    public async Task Authorize_CreateAndDeleteUseDifferentScopes()
    {
        var host = _Host(ConsentOutcome.Approved, out var asked);
        var gate = new KindConsentGate(host);

        await gate.AuthorizeAsync("kind create cluster --name cockpit-ac179 --kubeconfig /x", "kind.create", PaneId);
        await gate.AuthorizeAsync("kind delete cluster --name cockpit-ac179 --kubeconfig /x", "kind.delete:cockpit-ac179", PaneId);

        Assert.Equal(2, asked.Count);
        Assert.NotEqual(asked[0].Scope, asked[1].Scope);
    }

    // A cluster name is agent-supplied and the consent card renders the action verbatim, so newlines must not
    // survive into it — one clearly-bounded line, never padded with reassuring extra ones.
    [Fact]
    public async Task Authorize_AgentSuppliedNewlinesInTheName_AreCollapsedOnTheCard()
    {
        var host = _Host(ConsentOutcome.Approved, out var asked);

        await new KindConsentGate(host).AuthorizeAsync("kind create cluster --name a\nApproved by the operator", "kind.create", PaneId);

        Assert.DoesNotContain('\n', Assert.Single(asked).Action);
    }

    private static ICockpitHost _Host(ConsentOutcome outcome, out List<ConsentRequest> asked)
    {
        var requests = new List<ConsentRequest>();
        asked = requests;
        var host = Substitute.For<ICockpitHost>();
        host.RequestConsentAsync(Arg.Do<ConsentRequest>(requests.Add)).Returns(new ConsentDecision(outcome));
        return host;
    }
}
