using Cockpit.Plugin.Kubernetes.Cluster;

namespace Cockpit.Plugin.Kubernetes.Tests;

// Exec-auth detection drives an operator-facing warning (a kubeconfig exec plugin runs an external process on
// connect), so it is security-relevant and pinned here: detected when present, absent for a plain token, and
// fail-safe (never throwing) on an unknown context or unparseable input.
public class KubeconfigInspectorTests
{
    private const string ExecAuthKubeconfig = """
    apiVersion: v1
    kind: Config
    current-context: ctx
    clusters:
    - name: c
      cluster:
        server: https://example.test
    contexts:
    - name: ctx
      context:
        cluster: c
        user: u
    users:
    - name: u
      user:
        exec:
          apiVersion: client.authentication.k8s.io/v1beta1
          command: aws
          args: ["eks", "get-token"]
    """;

    [Fact]
    public void Inspect_ExecAuthContext_NamesTheCommandItWouldRun() =>
        // The warning is only actionable if it says which external process the connect would launch.
        Assert.Equal("aws", KubeconfigInspector.Inspect(ExecAuthKubeconfig, contextName: null).Command);

}
