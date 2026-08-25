using Cockpit.Plugin.Kubernetes.Cli;

namespace Cockpit.Plugin.Kubernetes.Kind;

// Builds the three kind invocations this plugin makes (AC-179), argv only — never a shell string, since a cluster
// name is agent-supplied. Unlike HelmCommand, no locked-down Environment: KIND_EXPERIMENTAL_PROVIDER (docker vs
// podman) is the operator's own choice of container runtime and must reach the process untouched, not overridden.
internal static class KindCommand
{
    private static readonly IReadOnlyDictionary<string, string> NoEnvironmentOverride = new Dictionary<string, string>();

    public static CliCommand Create(string kindExecutablePath, string name, string kubeconfigPath) =>
        new(kindExecutablePath, ["create", "cluster", "--name", name, "--kubeconfig", kubeconfigPath], NoEnvironmentOverride);

    public static CliCommand Delete(string kindExecutablePath, string name, string kubeconfigPath) =>
        new(kindExecutablePath, ["delete", "cluster", "--name", name, "--kubeconfig", kubeconfigPath], NoEnvironmentOverride);

    public static CliCommand GetClusters(string kindExecutablePath) =>
        new(kindExecutablePath, ["get", "clusters"], NoEnvironmentOverride);
}
