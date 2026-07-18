using Cockpit.Plugin.Kubernetes.Cluster;
using FluentAssertions;

namespace Cockpit.Plugin.Kubernetes.Tests;

/// <summary>
/// Exec-auth detection drives an operator-facing warning (a kubeconfig exec plugin runs an external process on
/// connect), so it is security-relevant and pinned here: detected when present, absent for a plain token, and
/// fail-safe (never throwing) on an unknown context or unparseable input.
/// </summary>
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

    private const string TokenKubeconfig = """
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
        token: a-static-token
    """;

    [Fact]
    public void Inspect_ExecAuthContext_IsDetected()
    {
        var info = KubeconfigInspector.Inspect(ExecAuthKubeconfig, contextName: null);
        info.UsesExecAuth.Should().BeTrue();
        info.Command.Should().Be("aws");
    }

    [Fact]
    public void Inspect_TokenContext_IsNotExecAuth() =>
        KubeconfigInspector.Inspect(TokenKubeconfig, contextName: null).UsesExecAuth.Should().BeFalse();

    [Fact]
    public void Inspect_BlankContext_FallsBackToCurrentContext() =>
        KubeconfigInspector.Inspect(ExecAuthKubeconfig, contextName: "").UsesExecAuth.Should().BeTrue();

    [Fact]
    public void Inspect_UnknownContext_IsNotExecAuth() =>
        KubeconfigInspector.Inspect(ExecAuthKubeconfig, contextName: "no-such-context").UsesExecAuth.Should().BeFalse();

    [Fact]
    public void Inspect_UnparseableYaml_DoesNotThrow() =>
        KubeconfigInspector.Inspect("this: is: not: valid: kubeconfig: [", contextName: null).UsesExecAuth.Should().BeFalse();
}
