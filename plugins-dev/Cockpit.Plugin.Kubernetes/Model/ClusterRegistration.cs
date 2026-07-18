namespace Cockpit.Plugin.Kubernetes.Model;

/// <summary>
/// One registered cluster (AC-80): the non-secret metadata the plugin keeps in <see cref="Settings.KubernetesSettings"/>.
/// The kubeconfig itself is never here — it lives under the host's secret layer, keyed by <see cref="Id"/> — so this
/// record can be serialized to <c>cockpit.json</c> without carrying a credential.
/// <para>
/// <see cref="AllowedNamespaces"/> is the cluster's namespace jail: those are reachable without asking, and anything
/// outside it — reads included — asks for consent each session. The capability flags default off; a change to a
/// cluster-scoped resource, or exec/port-forward/attach, only happens on a cluster where the operator turned it on.
/// </para>
/// </summary>
/// <param name="Id">Stable id, also the key the pasted kubeconfig is stored under (<c>cluster.{Id}.kubeconfig</c>).</param>
/// <param name="Label">Friendly name shown in prompts and the settings list.</param>
/// <param name="ContextName">Which kubeconfig context to use; blank means the file's current-context.</param>
/// <param name="AllowedNamespaces">The namespaces an agent may reach without a per-access consent prompt.</param>
/// <param name="AllowClusterScoped">Whether cluster-scoped resources (nodes, PVs, namespaces, cluster roles) may be reached at all — they sit outside every namespace jail. Off by default.</param>
/// <param name="AllowExec">Whether <c>exec</c> (a command in a pod) is offered for this cluster. Off by default.</param>
/// <param name="AllowPortForward">Whether <c>port-forward</c> (a tunnel into the cluster) is offered for this cluster. Off by default.</param>
/// <param name="AllowAttach">Whether <c>attach</c> (attaching to a running container) is offered for this cluster. Off by default.</param>
/// <param name="UsesExecAuth">Whether the chosen context authenticates via a kubeconfig exec credential plugin (e.g. aws/gke) — connecting then runs an external process, so the operator is warned. Detected when the cluster is saved.</param>
/// <param name="KubeconfigPath">A kubeconfig file to read live on each connect (e.g. <c>~/.kube/config</c>); blank means use the pasted kubeconfig stored under the secret layer instead. Operator-supplied, never agent input.</param>
public sealed record ClusterRegistration(
    string Id,
    string Label,
    string ContextName,
    IReadOnlyList<string> AllowedNamespaces,
    bool AllowClusterScoped = false,
    bool AllowExec = false,
    bool AllowPortForward = false,
    bool AllowAttach = false,
    bool UsesExecAuth = false,
    string KubeconfigPath = "")
{
    public bool IsNamespaceAllowed(string @namespace) =>
        AllowedNamespaces.Any(allowed => string.Equals(allowed, @namespace, StringComparison.Ordinal));
}
