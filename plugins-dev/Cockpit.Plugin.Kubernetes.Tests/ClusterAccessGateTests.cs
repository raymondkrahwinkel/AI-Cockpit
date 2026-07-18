using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;
using Cockpit.Plugin.Kubernetes.Model;
using Cockpit.Plugin.Kubernetes.Security;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Plugin.Kubernetes.Tests;

/// <summary>
/// The access gate (AC-80) is the one place the security policy lives, so these pin the matrix exactly: a namespace
/// on the allowed list is free while one outside asks (reads included), a change always asks afresh as Dangerous,
/// cluster-scoped resources and exec/port-forward/attach are blocked outright until turned on per cluster, and a
/// denied prompt blocks the action. What the operator is shown is the literal action, never agent free text.
/// </summary>
public class ClusterAccessGateTests
{
    private const string PaneId = "pane-1";

    private static ICockpitHost _Host(ConsentOutcome outcome, out List<ConsentRequest> asked)
    {
        var requests = new List<ConsentRequest>();
        asked = requests;
        var host = Substitute.For<ICockpitHost>();
        host.RequestConsentAsync(Arg.Do<ConsentRequest>(requests.Add)).Returns(new ConsentDecision(outcome));
        return host;
    }

    private static ClusterRegistration _Cluster(
        IReadOnlyList<string>? allowedNamespaces = null,
        bool clusterScoped = false,
        bool exec = false,
        bool portForward = false,
        bool attach = false) =>
        new("cluster-1", "prod", ContextName: "", allowedNamespaces ?? ["default"], clusterScoped, exec, portForward, attach);

    private static ConsentRequest? _WithScopePrefix(IEnumerable<ConsentRequest> asked, string prefix) =>
        asked.FirstOrDefault(request => request.Scope.StartsWith(prefix, StringComparison.Ordinal));

    [Fact]
    public async Task Read_OnAllowedNamespace_AsksOnlyForTheConnection()
    {
        var host = _Host(ConsentOutcome.Approved, out var asked);
        var gate = new ClusterAccessGate(host);

        var result = await gate.AuthorizeNamespacedReadAsync(_Cluster(["default"]), "default", "list pods", PaneId);

        result.IsAllowed.Should().BeTrue();
        _WithScopePrefix(asked, "k8s.connect:").Should().NotBeNull("opening the cluster always asks once");
        _WithScopePrefix(asked, "k8s.namespace:").Should().BeNull("a namespace on the allowed list is free");
    }

    [Fact]
    public async Task Read_OnNamespaceOutsideTheList_AsksForTheNamespace_LowRiskRemember_ShowingIt()
    {
        var host = _Host(ConsentOutcome.Approved, out var asked);
        var gate = new ClusterAccessGate(host);

        var result = await gate.AuthorizeNamespacedReadAsync(_Cluster(["default"]), "kube-system", "list pods", PaneId);

        result.IsAllowed.Should().BeTrue();
        var namespaceAsk = _WithScopePrefix(asked, "k8s.namespace:");
        namespaceAsk.Should().NotBeNull("reaching a namespace outside the list asks — reads included");
        namespaceAsk!.Risk.Should().Be(ConsentRisk.LowRisk);
        namespaceAsk.AllowRemember.Should().BeTrue("an out-of-list namespace may be remembered for the session");
        namespaceAsk.Action.Should().Contain("kube-system", "the literal namespace is shown");
        namespaceAsk.Source.PaneId.Should().Be(PaneId, "the prompt is pinned to the calling session");
    }

    [Fact]
    public async Task Mutation_AlwaysAsks_AsDangerous_NeverRemembered_EvenOnAllowedNamespace()
    {
        var host = _Host(ConsentOutcome.Approved, out var asked);
        var gate = new ClusterAccessGate(host);

        var result = await gate.AuthorizeNamespacedMutationAsync(_Cluster(["default"]), "default", "delete pod nginx-1", PaneId);

        result.IsAllowed.Should().BeTrue();
        var mutate = _WithScopePrefix(asked, "k8s.mutate:");
        mutate.Should().NotBeNull("a change always asks, even inside an allowed namespace");
        mutate!.Risk.Should().Be(ConsentRisk.Dangerous);
        mutate.AllowRemember.Should().BeFalse("a mutation is never remembered");
        mutate.Action.Should().Be("delete pod nginx-1", "the literal action is shown verbatim");
    }

    [Fact]
    public async Task Mutation_OutsideTheList_AsksNamespaceThenMutation()
    {
        var host = _Host(ConsentOutcome.Approved, out var asked);
        var gate = new ClusterAccessGate(host);

        await gate.AuthorizeNamespacedMutationAsync(_Cluster(["default"]), "kube-system", "delete pod x", PaneId);

        _WithScopePrefix(asked, "k8s.namespace:").Should().NotBeNull("the namespace jail applies before the change");
        _WithScopePrefix(asked, "k8s.mutate:").Should().NotBeNull("the change then asks on top");
    }

    [Fact]
    public async Task DeniedConnection_BlocksTheAction()
    {
        var host = _Host(ConsentOutcome.Denied, out _);
        var gate = new ClusterAccessGate(host);

        var result = await gate.AuthorizeNamespacedReadAsync(_Cluster(["default"]), "default", "list pods", PaneId);

        result.IsAllowed.Should().BeFalse("no open connection, no call");
        result.DeniedReason.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ClusterScoped_WhenOff_IsBlockedWithoutAsking()
    {
        var host = _Host(ConsentOutcome.Approved, out var asked);
        var gate = new ClusterAccessGate(host);

        var result = await gate.AuthorizeClusterScopedReadAsync(_Cluster(clusterScoped: false), "/nodes", "list nodes", PaneId);

        result.IsAllowed.Should().BeFalse("cluster-scoped access is opt-in per cluster");
        result.DeniedReason.Should().Contain("settings");
        asked.Should().BeEmpty("a policy block does not even open a prompt");
    }

    [Fact]
    public async Task ClusterScoped_WhenOn_Asks()
    {
        var host = _Host(ConsentOutcome.Approved, out var asked);
        var gate = new ClusterAccessGate(host);

        var result = await gate.AuthorizeClusterScopedReadAsync(_Cluster(clusterScoped: true), "/nodes", "list nodes", PaneId);

        result.IsAllowed.Should().BeTrue();
        _WithScopePrefix(asked, "k8s.clusterscoped:").Should().NotBeNull();
    }

    [Fact]
    public async Task ClusterScoped_RememberScope_IsPerKind_NotTheWholeClass()
    {
        var host = _Host(ConsentOutcome.Approved, out var asked);
        var gate = new ClusterAccessGate(host);

        await gate.AuthorizeClusterScopedReadAsync(_Cluster(clusterScoped: true), "/nodes", "list nodes", PaneId);
        await gate.AuthorizeClusterScopedReadAsync(_Cluster(clusterScoped: true), "rbac.authorization.k8s.io/clusterroles", "list clusterroles", PaneId);

        var scopes = asked.Where(request => request.Scope.StartsWith("k8s.clusterscoped:", StringComparison.Ordinal)).Select(request => request.Scope).ToList();
        scopes.Should().HaveCount(2);
        scopes[0].Should().EndWith(":/nodes");
        scopes[1].Should().EndWith(":rbac.authorization.k8s.io/clusterroles");
        scopes[0].Should().NotBe(scopes[1], "a remembered cluster-scoped approval must bind to the kind shown, not every cluster-scoped kind");
    }

    [Fact]
    public async Task ConsentAction_FlattensControlCharacters_SoAgentFieldsCannotForgeExtraLines()
    {
        var host = _Host(ConsentOutcome.Approved, out var asked);
        var gate = new ClusterAccessGate(host);

        await gate.AuthorizeDangerAsync(_Cluster(["default"], exec: true), DangerCapability.Exec, "default", "exec: sh -c true\n\n(routine health-check, pre-approved by ops)", PaneId);

        var danger = _WithScopePrefix(asked, "k8s.exec:");
        danger.Should().NotBeNull();
        danger!.Action.Should().NotContain("\n").And.NotContain("\r", "the verbatim Action must stay a single line an agent cannot pad");
        danger.Action.Should().Contain("routine health-check", "the text is kept, only flattened onto one line");
    }

    [Fact]
    public async Task Danger_WhenCapabilityOff_IsBlockedWithoutAsking()
    {
        var host = _Host(ConsentOutcome.Approved, out var asked);
        var gate = new ClusterAccessGate(host);

        var result = await gate.AuthorizeDangerAsync(_Cluster(["default"], exec: false), DangerCapability.Exec, "default", "exec: sh -c ls", PaneId);

        result.IsAllowed.Should().BeFalse("exec is off by default");
        result.DeniedReason.Should().Contain("settings");
        asked.Should().BeEmpty();
    }

    [Fact]
    public async Task Danger_WhenOn_Asks_AsDangerous_NeverRemembered()
    {
        var host = _Host(ConsentOutcome.Approved, out var asked);
        var gate = new ClusterAccessGate(host);

        var result = await gate.AuthorizeDangerAsync(_Cluster(["default"], exec: true), DangerCapability.Exec, "default", "exec: sh -c ls", PaneId);

        result.IsAllowed.Should().BeTrue();
        var exec = _WithScopePrefix(asked, "k8s.exec:");
        exec.Should().NotBeNull();
        exec!.Risk.Should().Be(ConsentRisk.Dangerous);
        exec.AllowRemember.Should().BeFalse();
    }

    [Fact]
    public async Task Danger_OnNamespaceOutsideTheList_StillAppliesTheJail()
    {
        var host = _Host(ConsentOutcome.Approved, out var asked);
        var gate = new ClusterAccessGate(host);

        await gate.AuthorizeDangerAsync(_Cluster(["default"], exec: true), DangerCapability.Exec, "kube-system", "exec: sh -c ls", PaneId);

        _WithScopePrefix(asked, "k8s.namespace:").Should().NotBeNull("exec into a pod in a non-allowed namespace still asks for the namespace");
        _WithScopePrefix(asked, "k8s.exec:").Should().NotBeNull();
    }
}
