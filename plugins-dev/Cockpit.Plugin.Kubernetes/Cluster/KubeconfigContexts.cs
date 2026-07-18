namespace Cockpit.Plugin.Kubernetes.Cluster;

/// <summary>The contexts found in a kubeconfig, with which one is its current-context — what the settings UI offers in the context dropdown.</summary>
internal sealed record KubeconfigContexts(IReadOnlyList<string> Names, string? Current);
