using Cockpit.Plugin.Kubernetes.Helm;
using Cockpit.Plugin.Kubernetes.Model;

namespace Cockpit.Plugin.Kubernetes.Tests;

// `HelmCommand.Build` (AC-1061 phase 5, AC 5 & 11): the cluster must always win over whatever the ambient process
// environment says, and a pasted kubeconfig must be refused rather than staged to disk.
public class HelmCommandTests
{
    [Fact]
    public void Build_DerivesKubeContextAndKubeconfigFromTheCluster_RegardlessOfAmbientEnvironment()
    {
        // A machine with four contexts, none current, one of them production — exactly the case the ticket names.
        Environment.SetEnvironmentVariable("HELM_KUBECONTEXT", "attacker-context");
        Environment.SetEnvironmentVariable("HELM_NAMESPACE", "attacker-namespace");
        Environment.SetEnvironmentVariable("HELM_KUBETOKEN", "attacker-token");
        try
        {
            var cluster = new ClusterRegistration("id-1", "eveworkbench-cluster", ContextName: "eveworkbench-prod", ["system-ingress"], KubeconfigPath: "/etc/cockpit/eveworkbench.kubeconfig");

            var (command, error) = HelmCommand.Build("helm", cluster, "system-ingress", "upgrade", ["cert-manager", "cert-manager/cert-manager"]);

            Assert.Null(error);
            Assert.NotNull(command);
            Assert.Equal(
                ["upgrade", "--kube-context", "eveworkbench-prod", "--kubeconfig", "/etc/cockpit/eveworkbench.kubeconfig", "-n", "system-ingress", "cert-manager", "cert-manager/cert-manager"],
                command.Arguments);

            // The built command's own environment overrides — not whatever happens to be ambient — is what a
            // spawned process would actually see (HelmRunner layers these onto the inherited environment).
            Assert.Equal(string.Empty, command.Environment["HELM_KUBECONTEXT"]);
            Assert.Equal(string.Empty, command.Environment["HELM_NAMESPACE"]);
            Assert.Equal(string.Empty, command.Environment["HELM_KUBETOKEN"]);
            Assert.Equal(string.Empty, command.Environment["HELM_KUBEAPISERVER"]);
            Assert.Equal(string.Empty, command.Environment["HELM_KUBECAFILE"]);
            Assert.Equal("secret", command.Environment["HELM_DRIVER"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HELM_KUBECONTEXT", null);
            Environment.SetEnvironmentVariable("HELM_NAMESPACE", null);
            Environment.SetEnvironmentVariable("HELM_KUBETOKEN", null);
        }
    }

    [Fact]
    public void Build_BlankContextName_OmitsTheFlag_ButKubeconfigStaysExplicit()
    {
        var cluster = new ClusterRegistration("id-1", "prod", ContextName: "", ["default"], KubeconfigPath: "~/.kube/config");

        var (command, error) = HelmCommand.Build("helm", cluster, "default", "upgrade", ["web", "chart"]);

        Assert.Null(error);
        Assert.DoesNotContain("--kube-context", command!.Arguments);
        Assert.Contains("--kubeconfig", command.Arguments);
    }

    [Fact]
    public void Build_PastedKubeconfigCluster_ReturnsAnExplicitError_AndNoCommand()
    {
        var cluster = new ClusterRegistration("id-1", "prod", ContextName: "prod-ctx", ["default"], KubeconfigPath: "");

        var (command, error) = HelmCommand.Build("helm", cluster, "default", "upgrade", ["web", "chart"]);

        Assert.Null(command);
        Assert.NotNull(error);
        Assert.Contains("pasted kubeconfig", error);
    }
}
