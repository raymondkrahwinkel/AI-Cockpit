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
    public void Inspect_ExecAuthContext_NamesTheCommandItWouldRun() =>
        // The warning is only actionable if it says which external process the connect would launch.
        Assert.Equal("aws", KubeconfigInspector.Inspect(ExecAuthKubeconfig, contextName: null).Command);

    public static IEnumerable<object[]> Kubeconfigs() =>
    [
        [ExecAuthKubeconfig, null!, true],
        [TokenKubeconfig, null!, false],
        // A blank context name falls back to the file's current-context.
        [ExecAuthKubeconfig, "", true],
        // Fail-safe: an unknown context or unreadable input never throws, and never claims exec-auth it did not see.
        [ExecAuthKubeconfig, "no-such-context", false],
        ["this: is: not: valid: kubeconfig: [", null!, false],
    ];

    [Theory]
    [MemberData(nameof(Kubeconfigs))]
    public void Inspect_ReportsExecAuth_ForTheContextItResolved(string yaml, string? contextName, bool usesExecAuth) =>
        Assert.Equal(usesExecAuth, KubeconfigInspector.Inspect(yaml, contextName).UsesExecAuth);

    private const string MultiContextKubeconfig = """
    apiVersion: v1
    kind: Config
    current-context: prod
    clusters:
    - name: c1
      cluster:
        server: https://a.test
    - name: c2
      cluster:
        server: https://b.test
    contexts:
    - name: dev
      context:
        cluster: c1
        user: u
    - name: prod
      context:
        cluster: c2
        user: u
    users:
    - name: u
      user:
        token: t
    """;

    [Fact]
    public void ListContexts_ReturnsNamesAndCurrent()
    {
        var contexts = KubeconfigInspector.ListContexts(MultiContextKubeconfig);
        Assert.Equal(new[] { "dev", "prod" }, contexts.Names);
        Assert.Equal("prod", contexts.Current);
    }

    [Fact]
    public void ListContexts_Unparseable_IsEmpty() =>
        Assert.Empty(KubeconfigInspector.ListContexts("not a kubeconfig [").Names);

    [Fact]
    public void ExpandPath_ExpandsLeadingTilde()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.Combine(home, ".kube/config"), KubeconfigInspector.ExpandPath("~/.kube/config"));
        Assert.Equal("/etc/kube/config", KubeconfigInspector.ExpandPath("/etc/kube/config"));
    }

    [Fact]
    public void ReadYaml_PrefersThePath_ThenContent_ThenNull()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"kubetest-{Guid.NewGuid():n}.yaml");
        File.WriteAllText(tmp, "from-file");
        try
        {
            Assert.Equal("from-file", KubeconfigInspector.ReadYaml(tmp, "from-content"));
            Assert.Equal("from-content", KubeconfigInspector.ReadYaml(null, "from-content"));
            Assert.Null(KubeconfigInspector.ReadYaml("", ""));
            Assert.Null(KubeconfigInspector.ReadYaml("/no/such/file/at/all", null));
        }
        finally
        {
            File.Delete(tmp);
        }
    }
}
