
namespace Cockpit.Plugin.Kind.Tests;

// argv assembly for the three kind invocations this plugin makes (AC-179), mirroring HelmCommandTests. Argv only,
// never a shell string — a cluster name is agent-supplied. No locked-down environment: unlike helm's env vars,
// KIND_EXPERIMENTAL_PROVIDER (docker vs podman) is the operator's own choice and must pass through untouched.
public class KindCommandTests
{
    [Fact]
    public void Create_BuildsCreateClusterArgvWithNameAndKubeconfig()
    {
        var command = KindCommand.Create("kind", "cockpit-ac179", "/state/kind/cockpit-ac179.kubeconfig");

        Assert.Equal("kind", command.FileName);
        Assert.Equal(["create", "cluster", "--name", "cockpit-ac179", "--kubeconfig", "/state/kind/cockpit-ac179.kubeconfig"], command.Arguments);
        Assert.Empty(command.Environment);
    }

    [Fact]
    public void Delete_BuildsDeleteClusterArgvWithNameAndKubeconfig()
    {
        var command = KindCommand.Delete("kind", "cockpit-ac179", "/state/kind/cockpit-ac179.kubeconfig");

        Assert.Equal("kind", command.FileName);
        Assert.Equal(["delete", "cluster", "--name", "cockpit-ac179", "--kubeconfig", "/state/kind/cockpit-ac179.kubeconfig"], command.Arguments);
    }

    [Fact]
    public void GetClusters_BuildsGetClustersArgv()
    {
        var command = KindCommand.GetClusters("kind");

        Assert.Equal(["get", "clusters"], command.Arguments);
    }
}
