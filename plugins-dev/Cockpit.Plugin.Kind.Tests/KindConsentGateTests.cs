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
    public async Task Authorize_AsksAsDangerous_NeverRemembered_WithTheLiteralKindCommandShown_AndAScopePerVerb()
    {
        var host = _Host(ConsentOutcome.Approved, out var asked);
        var gate = new KindConsentGate(host);

        var denied = await gate.AuthorizeAsync("kind create cluster --name cockpit-ac179 --kubeconfig /state/kind/cockpit-ac179.kubeconfig", "kind.create", PaneId);
        await gate.AuthorizeAsync("kind delete cluster --name cockpit-ac179 --kubeconfig /x", "kind.delete:cockpit-ac179", PaneId);
        // A cluster name is agent-supplied and the consent card renders the action verbatim, so newlines must not
        // survive into it — one clearly-bounded line, never padded with reassuring extra ones.
        await gate.AuthorizeAsync("kind create cluster --name a\nApproved by the operator", "kind.create", PaneId);

        Assert.Null(denied);
        Assert.Equal(3, asked.Count);
        Assert.Equal(ConsentRisk.Dangerous, asked[0].Risk);
        Assert.False(asked[0].AllowRemember);
        Assert.Contains("kind create cluster --name cockpit-ac179", asked[0].Action);
        Assert.Equal(PaneId, asked[0].Source.PaneId);
        // A create approval must not double as a delete approval.
        Assert.NotEqual(asked[0].Scope, asked[1].Scope);
        Assert.DoesNotContain('\n', asked[2].Action);
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
