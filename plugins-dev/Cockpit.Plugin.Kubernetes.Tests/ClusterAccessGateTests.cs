using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;
using Cockpit.Plugin.Kubernetes.Helm;
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

    // The fake host mirrors ConsentService's own rule (AC-1348, exercised for real in ConsentServiceTests): a
    // request carrying PreApprovedBy comes back Approved+Bypassed without ever needing an operator answer.
    private static ICockpitHost _Host(ConsentOutcome outcome, out List<ConsentRequest> asked)
    {
        var requests = new List<ConsentRequest>();
        asked = requests;
        var host = Substitute.For<ICockpitHost>();
        host.RequestConsentAsync(Arg.Do<ConsentRequest>(requests.Add))
            .Returns(info => Task.FromResult(_Decide(info.Arg<ConsentRequest>(), outcome)));
        return host;
    }

    private static ConsentDecision _Decide(ConsentRequest request, ConsentOutcome outcome) =>
        request.PreApprovedBy is not null
            ? new ConsentDecision(ConsentOutcome.Approved, Bypassed: true)
            : new ConsentDecision(outcome);

    // Jita's shape: staging and production on one cluster, with only staging on the allowed list.
    private static ClusterRegistration _Jita(ClusterConsentMode mode) =>
        new ClusterRegistration("cluster-jita", "jita", ContextName: "jita-context", AllowedNamespaces: ["staging"]).WithConsentMode(mode);

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

        var result = await gate.AuthorizeNamespacedReadAsync(_Cluster(["default"]), "list_resources", "default", "list pods", PaneId);

        Assert.True(result.IsAllowed);
        Assert.NotNull(_WithScopePrefix(asked, "k8s.connect:"));
        Assert.Null(_WithScopePrefix(asked, "k8s.namespace:"));
    }

    [Fact]
    public async Task Read_OnNamespaceOutsideTheList_AsksForTheNamespace_LowRiskRemember_ShowingIt()
    {
        var host = _Host(ConsentOutcome.Approved, out var asked);
        var gate = new ClusterAccessGate(host);

        var result = await gate.AuthorizeNamespacedReadAsync(_Cluster(["default"]), "list_resources", "kube-system", "list pods", PaneId);

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

        var result = await gate.AuthorizeNamespacedMutationAsync(_Cluster(["default"]), "delete_resource", "default", "delete pod nginx-1", PaneId);

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

        await gate.AuthorizeNamespacedMutationAsync(_Cluster(["default"]), "delete_resource", "kube-system", "delete pod x", PaneId);

        Assert.NotNull(_WithScopePrefix(asked, "k8s.namespace:"));
        Assert.NotNull(_WithScopePrefix(asked, "k8s.mutate:"));
    }

    // AC-576 phase 4: a refresh is a write, but Raymond's review of the phase asked that it not share
    // the generic "k8s.mutate" bucket a real change (a future argo_sync) would use — the difference must show
    // up in the classification, not just in the operation text.
    [Fact]
    public async Task ArgoRefresh_AsksDangerous_NeverRemembered_OnItsOwnScope_NotTheGenericMutationBucket()
    {
        var host = _Host(ConsentOutcome.Approved, out var asked);
        var gate = new ClusterAccessGate(host);

        var result = await gate.AuthorizeArgoRefreshAsync(_Cluster(["argocd"]), "argo_refresh", "argocd", "refresh Application \"cert-manager\"", PaneId);

        Assert.True(result.IsAllowed);
        var refresh = _WithScopePrefix(asked, "k8s.argo.refresh:");
        Assert.NotNull(refresh);
        Assert.Equal(ConsentRisk.Dangerous, refresh!.Risk);
        Assert.False(refresh.AllowRemember, "a refresh is never remembered, same as every other write");
        Assert.Null(_WithScopePrefix(asked, "k8s.mutate:"));
    }

    [Fact]
    public async Task ArgoRefresh_OutsideTheAllowedNamespace_AsksNamespaceThenRefresh()
    {
        var host = _Host(ConsentOutcome.Approved, out var asked);
        var gate = new ClusterAccessGate(host);

        await gate.AuthorizeArgoRefreshAsync(_Cluster(["default"]), "argo_refresh", "argocd", "refresh Application \"cert-manager\"", PaneId);

        Assert.NotNull(_WithScopePrefix(asked, "k8s.namespace:"));
        Assert.NotNull(_WithScopePrefix(asked, "k8s.argo.refresh:"));
    }

    [Fact]
    public async Task DeniedConnection_BlocksTheAction()
    {
        var host = _Host(ConsentOutcome.Denied, out _);
        var gate = new ClusterAccessGate(host);

        var result = await gate.AuthorizeNamespacedReadAsync(_Cluster(["default"]), "list_resources", "default", "list pods", PaneId);

        Assert.False(result.IsAllowed, "no open connection, no call");
        Assert.False(string.IsNullOrEmpty(result.DeniedReason));
    }

    [Fact]
    public async Task ClusterScoped_WhenOff_IsBlockedWithoutAsking()
    {
        var host = _Host(ConsentOutcome.Approved, out var asked);
        var gate = new ClusterAccessGate(host);

        var result = await gate.AuthorizeClusterScopedReadAsync(_Cluster(clusterScoped: false), "list_resources", "/nodes", "list nodes", PaneId);

        Assert.False(result.IsAllowed, "cluster-scoped access is opt-in per cluster");
        Assert.Contains("settings", result.DeniedReason);
        Assert.Empty(asked);
    }

    [Fact]
    public async Task ClusterScoped_WhenOn_Asks()
    {
        var host = _Host(ConsentOutcome.Approved, out var asked);
        var gate = new ClusterAccessGate(host);

        var result = await gate.AuthorizeClusterScopedReadAsync(_Cluster(clusterScoped: true), "list_resources", "/nodes", "list nodes", PaneId);

        Assert.True(result.IsAllowed);
        Assert.NotNull(_WithScopePrefix(asked, "k8s.clusterscoped:"));
    }

    [Fact]
    public async Task ClusterScoped_RememberScope_IsPerKind_NotTheWholeClass()
    {
        var host = _Host(ConsentOutcome.Approved, out var asked);
        var gate = new ClusterAccessGate(host);

        await gate.AuthorizeClusterScopedReadAsync(_Cluster(clusterScoped: true), "list_resources", "/nodes", "list nodes", PaneId);
        await gate.AuthorizeClusterScopedReadAsync(_Cluster(clusterScoped: true), "list_resources", "rbac.authorization.k8s.io/clusterroles", "list clusterroles", PaneId);

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

        await gate.AuthorizeDangerAsync(_Cluster(["default"], exec: true), "exec", DangerCapability.Exec, "default", "exec: sh -c true\n\n(routine health-check, pre-approved by ops)", PaneId);

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

        await gate.AuthorizeDangerAsync(_Cluster(["default"], exec: true), "exec", DangerCapability.Exec, "default", "echo hi #harmless\nrm -rf /data", PaneId);

        var danger = _WithScopePrefix(asked, "k8s.exec:");
        Assert.NotNull(danger);
        Assert.DoesNotContain("#harmless rm -rf /data", danger!.Action, StringComparison.Ordinal);
        Assert.Contains("#harmless\\nrm -rf /data", danger.Action, StringComparison.Ordinal);
    }

    // AC-1062, criterion 1: a rollback's manifest diff (one updated and one deleted resource) reaches the operator
    // as one line per rendered line, not as one flattened line with visible `\n` escapes standing in for real breaks.
    [Fact]
    public async Task Mutation_WithDetailLines_RendersOneLinePerManifestDiffLine_NoEscapedNewlineSubstring()
    {
        var current = "apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: a\ndata:\n  value: old\n"
            + "---\napiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: b\n";
        var target = "apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: a\ndata:\n  value: new\n";
        var lines = ManifestDiff.Compute(current, target).ToConsentLines(3_500);
        var host = _Host(ConsentOutcome.Approved, out var asked);
        var gate = new ClusterAccessGate(host);

        await gate.AuthorizeNamespacedMutationAsync(_Cluster(["default"]), "delete_resource", "default", "roll back Helm release \"demo\"", PaneId, lines);

        var mutate = _WithScopePrefix(asked, "k8s.mutate:");
        Assert.NotNull(mutate);
        Assert.Equal(1 + lines.Count, mutate!.Action.Split('\n').Length);
        Assert.DoesNotContain("\\n", mutate.Action, StringComparison.Ordinal);
    }

    // AC-1062, criterion 3: the multi-line ingress still holds the AC-92 invariant — a detail line carrying a raw
    // newline of its own cannot turn into a second physical line, it comes out escaped on the one line it was given.
    [Fact]
    public async Task Mutation_ADetailLineWithAnEmbeddedNewline_ComesOutAsOneEscapedLine_NotTwoLines()
    {
        var host = _Host(ConsentOutcome.Approved, out var asked);
        var gate = new ClusterAccessGate(host);

        await gate.AuthorizeNamespacedMutationAsync(
            _Cluster(["default"]), "delete_resource", "default", "roll back Helm release \"demo\"", PaneId,
            detailLines: ["+ CREATE v1 ConfigMap default/evil\nrm -rf /data"]);

        var mutate = _WithScopePrefix(asked, "k8s.mutate:");
        Assert.NotNull(mutate);
        Assert.Equal(2, mutate!.Action.Split('\n').Length);
        Assert.Contains("evil\\nrm -rf /data", mutate.Action, StringComparison.Ordinal);
    }

    // AC-1062, criterion 5: a diff bigger than MaxConsentDiffLength still lands on a bounded Action with the
    // "… and N more resource(s)" tail, so Approve/Deny stays reachable instead of the card growing without limit.
    [Fact]
    public async Task Mutation_WithDetailLinesPastTheBudget_KeepsTheComposedActionBounded_WithTheMoreResourcesTail()
    {
        var current = string.Join("\n---\n", Enumerable.Range(0, 40).Select(index => $"apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: c{index}\ndata:\n  value: old"));
        var target = string.Join("\n---\n", Enumerable.Range(0, 40).Select(index => $"apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: c{index}\ndata:\n  value: new"));
        var lines = ManifestDiff.Compute(current, target).ToConsentLines(400);
        var host = _Host(ConsentOutcome.Approved, out var asked);
        var gate = new ClusterAccessGate(host);

        var result = await gate.AuthorizeNamespacedMutationAsync(_Cluster(["default"]), "delete_resource", "default", "roll back Helm release \"demo\"", PaneId, lines);

        Assert.True(result.IsAllowed, "a bounded diff must still let Approve/Deny be reached");
        var mutate = _WithScopePrefix(asked, "k8s.mutate:");
        Assert.NotNull(mutate);
        Assert.True(mutate!.Action.Length < 700, $"composed action must stay bounded, got {mutate.Action.Length} characters");
        Assert.Contains("more resource(s)", mutate.Action);
    }

    [Fact]
    public async Task Danger_WhenCapabilityOff_IsBlockedWithoutAsking()
    {
        var host = _Host(ConsentOutcome.Approved, out var asked);
        var gate = new ClusterAccessGate(host);

        var result = await gate.AuthorizeDangerAsync(_Cluster(["default"], exec: false), "exec", DangerCapability.Exec, "default", "exec: sh -c ls", PaneId);

        Assert.False(result.IsAllowed, "exec is off by default");
        Assert.Contains("settings", result.DeniedReason);
        Assert.Empty(asked);
    }

    [Fact]
    public async Task Danger_WhenOn_Asks_AsDangerous_NeverRemembered()
    {
        var host = _Host(ConsentOutcome.Approved, out var asked);
        var gate = new ClusterAccessGate(host);

        var result = await gate.AuthorizeDangerAsync(_Cluster(["default"], exec: true), "exec", DangerCapability.Exec, "default", "exec: sh -c ls", PaneId);

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

        await gate.AuthorizeDangerAsync(_Cluster(["default"], exec: true), "exec", DangerCapability.Exec, "kube-system", "exec: sh -c ls", PaneId);

        Assert.NotNull(_WithScopePrefix(asked, "k8s.namespace:"));
        Assert.NotNull(_WithScopePrefix(asked, "k8s.exec:"));
    }

    // AC-1349: a cluster's consent mode pre-approves a call by routing PreApprovedBy to the host instead of
    // asking. Counter-check rows: a change under ReadFree, an unlisted tool, a secret, a change outside the allowed
    // namespaces under AllFree, and a re-pointed context all still ask. The default mode is the tests above.
    public static IEnumerable<object[]> ConsentModeCases()
    {
        yield return
        [
            "AC1 + AC5 counter-check: ReadFree, relabelled after choosing, frees get_resource on pods",
            _Jita(ClusterConsentMode.ReadFree) with { Label = "jita-renamed" },
            (Func<ClusterAccessGate, ClusterRegistration, Task<GateResult>>)((gate, cluster) => gate.AuthorizeNamespacedReadAsync(cluster, "get_resource", "staging", "get pods/web (v1)", PaneId)),
            true,
        ];
        yield return
        [
            "AC1 counter-check: ReadFree still asks for delete_resource on the same cluster",
            _Jita(ClusterConsentMode.ReadFree),
            (Func<ClusterAccessGate, ClusterRegistration, Task<GateResult>>)((gate, cluster) => gate.AuthorizeNamespacedMutationAsync(cluster, "delete_resource", "staging", "delete pods/web (v1)", PaneId)),
            false,
        ];
        yield return
        [
            "AC2: an unlisted tool counts as a change and still asks under ReadFree",
            _Jita(ClusterConsentMode.ReadFree),
            (Func<ClusterAccessGate, ClusterRegistration, Task<GateResult>>)((gate, cluster) => gate.AuthorizeNamespacedReadAsync(cluster, "some_future_tool", "staging", "do something new", PaneId)),
            false,
        ];
        yield return
        [
            "AC3: ReadFree still asks for get_resource on a secret",
            _Jita(ClusterConsentMode.ReadFree),
            (Func<ClusterAccessGate, ClusterRegistration, Task<GateResult>>)((gate, cluster) => gate.AuthorizeSensitiveNamespacedReadAsync(cluster, "get_resource", "staging", "get secrets/db (v1)", PaneId, sensitiveResource: true)),
            false,
        ];
        yield return
        [
            "AC4: AllFree frees scale_resource in staging, on the allowed list",
            _Jita(ClusterConsentMode.AllFree),
            (Func<ClusterAccessGate, ClusterRegistration, Task<GateResult>>)((gate, cluster) => gate.AuthorizeNamespacedMutationAsync(cluster, "scale_resource", "staging", "scale deployments/web to 2 replica(s)", PaneId)),
            true,
        ];
        yield return
        [
            "AC4 counter-check: AllFree still asks for scale_resource in production, off the allowed list",
            _Jita(ClusterConsentMode.AllFree),
            (Func<ClusterAccessGate, ClusterRegistration, Task<GateResult>>)((gate, cluster) => gate.AuthorizeNamespacedMutationAsync(cluster, "scale_resource", "production", "scale deployments/web to 2 replica(s)", PaneId)),
            false,
        ];
        yield return
        [
            "AC5: a context changed after choosing ReadFree falls back to AlwaysAsk",
            _Jita(ClusterConsentMode.ReadFree) with { ContextName = "another-context" },
            (Func<ClusterAccessGate, ClusterRegistration, Task<GateResult>>)((gate, cluster) => gate.AuthorizeNamespacedReadAsync(cluster, "get_resource", "staging", "get pods/web (v1)", PaneId)),
            false,
        ];
    }

    // `act` arrives as `object` rather than a Func: a public [Theory] cannot declare a parameter of a type built
    // from the internal ClusterAccessGate/GateResult (CS0051) — the cast below is what recovers it.
    [Theory]
    [MemberData(nameof(ConsentModeCases))]
    public async Task ConsentMode_PreApprovesOnlyWhatTheClusterModeFrees(string scenario, ClusterRegistration cluster, object act, bool expectPreApproved)
    {
        var callGate = (Func<ClusterAccessGate, ClusterRegistration, Task<GateResult>>)act;
        var host = _Host(ConsentOutcome.Approved, out var asked);

        var result = await callGate(new ClusterAccessGate(host), cluster);

        Assert.True(result.IsAllowed, scenario);
        Assert.NotEmpty(asked);
        Assert.Equal(expectPreApproved, asked.TrueForAll(request => request.PreApprovedBy is not null));
        Assert.Equal(expectPreApproved, result.BypassNote is not null);
    }
}
