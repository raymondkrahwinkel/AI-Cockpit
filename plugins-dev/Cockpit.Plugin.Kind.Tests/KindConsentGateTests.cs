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
    public async Task Authorize_AsksAsDangerous_NeverRemembered_WithTheLiteralKindCommandShown()
    {
        var host = _Host(ConsentOutcome.Approved, out var asked);

        var denied = await new KindConsentGate(host).AuthorizeAsync("kind create cluster --name cockpit-ac179 --kubeconfig /state/kind/cockpit-ac179.kubeconfig", "kind.create", PaneId);

        Assert.Null(denied);
        var ask = Assert.Single(asked);
        Assert.Equal(ConsentRisk.Dangerous, ask.Risk);
        Assert.False(ask.AllowRemember);
        Assert.Contains("kind create cluster --name cockpit-ac179", ask.Action);
        Assert.Equal(PaneId, ask.Source.PaneId);
    }

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

    [Fact]
    public async Task Authorize_DeniedConsent_ReturnsARefusalForTheAgent()
    {
        var host = _Host(ConsentOutcome.Denied, out _);

        var denied = await new KindConsentGate(host).AuthorizeAsync("kind delete cluster --name cockpit-ac179 --kubeconfig /x", "kind.delete:cockpit-ac179", PaneId);

        Assert.NotNull(denied);
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
