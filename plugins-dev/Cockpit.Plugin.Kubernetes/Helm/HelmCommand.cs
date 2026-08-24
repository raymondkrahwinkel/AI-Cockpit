using Cockpit.Plugin.Kubernetes.Cluster;
using Cockpit.Plugin.Kubernetes.Model;

namespace Cockpit.Plugin.Kubernetes.Helm;

// One helm invocation, built as argv (never a shell string — a release name or value comes from an agent) plus a
// locked-down environment (AC-1061 phase 5, AC 5): `--kube-context`/`--kubeconfig` are always derived from
// `cluster`, and the env vars that `helm env` shows can hijack the target cluster are cleared or pinned here.
internal sealed record HelmCommand(string FileName, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> Environment)
{
    // HELM_DRIVER is pinned to helm's own default rather than cleared to "": an ambient override here would point
    // release storage at a different backend entirely, not just a different cluster.
    private static readonly IReadOnlyDictionary<string, string> _LockedEnvironment = new Dictionary<string, string>
    {
        ["HELM_KUBECONTEXT"] = string.Empty,
        ["HELM_NAMESPACE"] = string.Empty,
        ["HELM_KUBEAPISERVER"] = string.Empty,
        ["HELM_KUBETOKEN"] = string.Empty,
        ["HELM_KUBECAFILE"] = string.Empty,
        ["HELM_DRIVER"] = "secret",
    };

    // Builds one invocation, or an error when the cluster cannot be reached from a subprocess. CLI tools are
    // v1-scoped to path-registered clusters (§2e): staging a pasted kubeconfig to a temp file for the subprocess
    // would break the plugin's promise that credentials never leave process memory, so this refuses explicitly.
    public static (HelmCommand? Command, string? Error) Build(
        string helmExecutablePath,
        ClusterRegistration cluster,
        string @namespace,
        string verb,
        IReadOnlyList<string> arguments)
    {
        if (string.IsNullOrWhiteSpace(cluster.KubeconfigPath))
        {
            return (null, $"Cluster \"{cluster.Label}\" is registered with a pasted kubeconfig; upgrading via the CLI needs a kubeconfig file path instead.");
        }

        var argv = new List<string> { verb };
        if (!string.IsNullOrWhiteSpace(cluster.ContextName))
        {
            argv.Add("--kube-context");
            argv.Add(cluster.ContextName);
        }

        argv.Add("--kubeconfig");
        argv.Add(KubeconfigInspector.ExpandPath(cluster.KubeconfigPath));
        argv.Add("-n");
        argv.Add(@namespace);
        argv.AddRange(arguments);

        return (new HelmCommand(helmExecutablePath, argv, _LockedEnvironment), null);
    }
}
