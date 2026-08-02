using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;
using Cockpit.Plugin.Kubernetes.Model;
using Cockpit.Plugin.Kubernetes.Security;
using NSubstitute;

namespace Cockpit.Plugin.Kubernetes.Tests;

// The access gate (AC-80) is the one place the security policy lives, so these pin the matrix exactly: a namespace
// on the allowed list is free while one outside asks (reads included), a change always asks afresh as Dangerous,
// cluster-scoped resources and exec/port-forward/attach are blocked outright until turned on per cluster, and a
// denied prompt blocks the action. What the operator is shown is the literal action, never agent free text.
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

        Assert.True(result.IsAllowed);
        Assert.NotNull(_WithScopePrefix(asked, "k8s.connect:"));
        Assert.Null(_WithScopePrefix(asked, "k8s.namespace:"));
    }

    [Fact]
    public async Task Read_OnNamespaceOutsideTheList_AsksForTheNamespace_LowRiskRemember_ShowingIt()
    {
        var host = _Host(ConsentOutcome.Approved, out var asked);
        var gate = new ClusterAccessGate(host);

        var result = await gate.AuthorizeNamespacedReadAsync(_Cluster(["default"]), "kube-system", "list pods", PaneId);

        Assert.True(result.IsAllowed);
        var namespaceAsk = _WithScopePrefix(asked, "k8s.namespace:");
        Assert.NotNull(namespaceAsk);
        Assert.Equal(ConsentRisk.LowRisk, namespaceAsk!.Risk);
        Assert.True(namespaceAsk.AllowRemember, "an out-of-list namespace may be remembered for the session");
        Assert.Contains("kube-system", namespaceAsk.Action);
        Assert.Equal(PaneId, namespaceAsk.Source.PaneId);
    }

    [Fact]
    public async Task Mutation_AlwaysAsks_AsDangerous_NeverRemembered_EvenOnAllowedNamespace()
    {
        var host = _Host(ConsentOutcome.Approved, out var asked);
        var gate = new ClusterAccessGate(host);

        var result = await gate.AuthorizeNamespacedMutationAsync(_Cluster(["default"]), "default", "delete pod nginx-1", PaneId);

        Assert.True(result.IsAllowed);
        var mutate = _WithScopePrefix(asked, "k8s.mutate:");
        Assert.NotNull(mutate);
        Assert.Equal(ConsentRisk.Dangerous, mutate!.Risk);
        Assert.False(mutate.AllowRemember, "a mutation is never remembered");
        Assert.Equal("delete pod nginx-1", mutate.Action);
    }

    [Fact]
    public async Task Mutation_OutsideTheList_AsksNamespaceThenMutation()
    {
        var host = _Host(ConsentOutcome.Approved, out var asked);
        var gate = new ClusterAccessGate(host);

        await gate.AuthorizeNamespacedMutationAsync(_Cluster(["default"]), "kube-system", "delete pod x", PaneId);

        Assert.NotNull(_WithScopePrefix(asked, "k8s.namespace:"));
        Assert.NotNull(_WithScopePrefix(asked, "k8s.mutate:"));
    }

    [Fact]
    public async Task DeniedConnection_BlocksTheAction()
    {
        var host = _Host(ConsentOutcome.Denied, out _);
        var gate = new ClusterAccessGate(host);

        var result = await gate.AuthorizeNamespacedReadAsync(_Cluster(["default"]), "default", "list pods", PaneId);

        Assert.False(result.IsAllowed, "no open connection, no call");
        Assert.False(string.IsNullOrEmpty(result.DeniedReason));
    }

    [Fact]
    public async Task ClusterScoped_WhenOff_IsBlockedWithoutAsking()
    {
        var host = _Host(ConsentOutcome.Approved, out var asked);
        var gate = new ClusterAccessGate(host);

        var result = await gate.AuthorizeClusterScopedReadAsync(_Cluster(clusterScoped: false), "/nodes", "list nodes", PaneId);

        Assert.False(result.IsAllowed, "cluster-scoped access is opt-in per cluster");
        Assert.Contains("settings", result.DeniedReason);
        Assert.Empty(asked);
    }

    [Fact]
    public async Task ClusterScoped_WhenOn_Asks()
    {
        var host = _Host(ConsentOutcome.Approved, out var asked);
        var gate = new ClusterAccessGate(host);

        var result = await gate.AuthorizeClusterScopedReadAsync(_Cluster(clusterScoped: true), "/nodes", "list nodes", PaneId);

        Assert.True(result.IsAllowed);
        Assert.NotNull(_WithScopePrefix(asked, "k8s.clusterscoped:"));
    }

    [Fact]
    public async Task ClusterScoped_RememberScope_IsPerKind_NotTheWholeClass()
    {
        var host = _Host(ConsentOutcome.Approved, out var asked);
        var gate = new ClusterAccessGate(host);

        await gate.AuthorizeClusterScopedReadAsync(_Cluster(clusterScoped: true), "/nodes", "list nodes", PaneId);
        await gate.AuthorizeClusterScopedReadAsync(_Cluster(clusterScoped: true), "rbac.authorization.k8s.io/clusterroles", "list clusterroles", PaneId);

        var scopes = asked.Where(request => request.Scope.StartsWith("k8s.clusterscoped:", StringComparison.Ordinal)).Select(request => request.Scope).ToList();
        Assert.Equal(2, System.Linq.Enumerable.Count(scopes));
        Assert.EndsWith(":/nodes", scopes[0]);
        Assert.EndsWith(":rbac.authorization.k8s.io/clusterroles", scopes[1]);
        Assert.NotEqual(scopes[1], scopes[0]);
    }

    [Fact]
    public async Task ConsentAction_EscapesControlCharactersVisibly_SoAgentFieldsCannotForgeExtraLines()
    {
        var host = _Host(ConsentOutcome.Approved, out var asked);
        var gate = new ClusterAccessGate(host);

        await gate.AuthorizeDangerAsync(_Cluster(["default"], exec: true), DangerCapability.Exec, "default", "exec: sh -c true\n\n(routine health-check, pre-approved by ops)", PaneId);

        var danger = _WithScopePrefix(asked, "k8s.exec:");
        Assert.NotNull(danger);
        Assert.DoesNotContain("\n", danger!.Action, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", danger.Action, StringComparison.Ordinal);
        Assert.Contains("\\n", danger.Action, StringComparison.Ordinal);
        Assert.Contains("routine health-check", danger.Action, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConsentAction_DoesNotLetASecondLineMasqueradeAsAComment()
    {
        // AC-92: a raw newline flattened to a space turns "echo hi #harmless\nrm -rf /data" into one line that reads
        // as commented-out while the apiserver still runs line 2. Visibly escaping the break keeps that impossible.
        var host = _Host(ConsentOutcome.Approved, out var asked);
        var gate = new ClusterAccessGate(host);

        await gate.AuthorizeDangerAsync(_Cluster(["default"], exec: true), DangerCapability.Exec, "default", "echo hi #harmless\nrm -rf /data", PaneId);

        var danger = _WithScopePrefix(asked, "k8s.exec:");
        Assert.NotNull(danger);
        Assert.DoesNotContain("#harmless rm -rf /data", danger!.Action, StringComparison.Ordinal);
        Assert.Contains("#harmless\\nrm -rf /data", danger.Action, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Danger_WhenCapabilityOff_IsBlockedWithoutAsking()
    {
        var host = _Host(ConsentOutcome.Approved, out var asked);
        var gate = new ClusterAccessGate(host);

        var result = await gate.AuthorizeDangerAsync(_Cluster(["default"], exec: false), DangerCapability.Exec, "default", "exec: sh -c ls", PaneId);

        Assert.False(result.IsAllowed, "exec is off by default");
        Assert.Contains("settings", result.DeniedReason);
        Assert.Empty(asked);
    }

    [Fact]
    public async Task Danger_WhenOn_Asks_AsDangerous_NeverRemembered()
    {
        var host = _Host(ConsentOutcome.Approved, out var asked);
        var gate = new ClusterAccessGate(host);

        var result = await gate.AuthorizeDangerAsync(_Cluster(["default"], exec: true), DangerCapability.Exec, "default", "exec: sh -c ls", PaneId);

        Assert.True(result.IsAllowed);
        var exec = _WithScopePrefix(asked, "k8s.exec:");
        Assert.NotNull(exec);
        Assert.Equal(ConsentRisk.Dangerous, exec!.Risk);
        Assert.False(exec.AllowRemember);
    }

    [Fact]
    public async Task Danger_OnNamespaceOutsideTheList_StillAppliesTheJail()
    {
        var host = _Host(ConsentOutcome.Approved, out var asked);
        var gate = new ClusterAccessGate(host);

        await gate.AuthorizeDangerAsync(_Cluster(["default"], exec: true), DangerCapability.Exec, "kube-system", "exec: sh -c ls", PaneId);

        Assert.NotNull(_WithScopePrefix(asked, "k8s.namespace:"));
        Assert.NotNull(_WithScopePrefix(asked, "k8s.exec:"));
    }
}
