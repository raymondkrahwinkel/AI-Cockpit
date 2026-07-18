using System.Text;
using k8s;

namespace Cockpit.Plugin.Kubernetes.Cluster;

/// <summary>
/// Reads a kubeconfig without connecting, to tell the operator one security-relevant thing at registration time:
/// does the chosen context authenticate through an <c>exec</c> credential plugin? Such a context runs an external
/// process (e.g. <c>aws eks get-token</c>, <c>gke-gcloud-auth-plugin</c>) the first time it connects, so a tampered
/// kubeconfig is a code-execution vector — the operator should opt into it knowing that.
/// </summary>
internal static class KubeconfigInspector
{
    public static ExecAuthInfo Inspect(string kubeconfigYaml, string? contextName)
    {
        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(kubeconfigYaml));
            var config = KubernetesClientConfiguration.LoadKubeConfig(stream);

            var wantedContext = string.IsNullOrWhiteSpace(contextName) ? config.CurrentContext : contextName;
            var context = config.Contexts?.FirstOrDefault(candidate => string.Equals(candidate.Name, wantedContext, StringComparison.Ordinal));
            if (context?.ContextDetails is null)
            {
                return new ExecAuthInfo(false, null);
            }

            var user = config.Users?.FirstOrDefault(candidate => string.Equals(candidate.Name, context.ContextDetails.User, StringComparison.Ordinal));
            var exec = user?.UserCredentials?.ExternalExecution;
            return exec is null ? new ExecAuthInfo(false, null) : new ExecAuthInfo(true, exec.Command);
        }
        catch (Exception)
        {
            // A kubeconfig we cannot parse is one we cannot vouch for either way; report "no exec detected" rather
            // than throw, and let the connection attempt surface any real problem with the file.
            return new ExecAuthInfo(false, null);
        }
    }
}
