
namespace Cockpit.Plugin.Kind.Tests;

// argv assembly for the three kind invocations this plugin makes (AC-179), mirroring HelmCommandTests. Argv only,
// never a shell string — a cluster name is agent-supplied. No locked-down environment: unlike helm's env vars,
// KIND_EXPERIMENTAL_PROVIDER (docker vs podman) is the operator's own choice and must pass through untouched.
public class KindCommandTests
{
    [Theory]
    [InlineData("create")]
    [InlineData("delete")]
    public void BuildsTheClusterArgvWithNameAndKubeconfig(string verb)
    {
        var command = verb == "create"
            ? KindCommand.Create("kind", "cockpit-ac179", "/state/kind/cockpit-ac179.kubeconfig")
            : KindCommand.Delete("kind", "cockpit-ac179", "/state/kind/cockpit-ac179.kubeconfig");

        Assert.Equal("kind", command.FileName);
        Assert.Equal([verb, "cluster", "--name", "cockpit-ac179", "--kubeconfig", "/state/kind/cockpit-ac179.kubeconfig"], command.Arguments);
        Assert.Empty(command.Environment);
    }
}
