using HostHeuristic = Cockpit.Core.Projects.ProjectResourceSecretPathHeuristic;
using PluginHeuristic = Cockpit.Plugin.Depot.ProjectDefinition.ProjectResourceSecretPathHeuristic;

namespace Cockpit.Plugin.Depot.Tests.ProjectDefinition;

// AC-612: the host (`HostHeuristic`) and this plugin's own mirrored copy (`PluginHeuristic`) must agree
// on every reference — they cannot share code (a plugin must not reference `Cockpit.Core`, AC-244), the
// same constraint `ProjectResourceScopeParityTests` already works around for AC-605's scope classifier.
public class ProjectResourceSecretPathParityTests
{
    public static IEnumerable<object[]> References()
    {
        yield return ["~/.ssh/id_rsa"];
        yield return ["~/.ssh/id_rsa.pub"];
        yield return ["~/.ssh/id_ed25519"];
        yield return ["~/.ssh/known_hosts"];
        yield return ["~/.ssh/config"];
        yield return ["~/.sshfoo/x"];
        yield return ["~/.SSH/ID_RSA"];
        yield return ["~/.gnupg/private-keys-v1.d/foo"];
        yield return ["~/.aws"];
        yield return ["~/.aws/"];
        yield return ["~/.aws/credentials"];
        yield return ["~/.kube/config"];
        yield return ["~/.docker/config.json"];
        yield return ["~/.docker/daemon.json"];
        yield return ["~/.netrc"];
        yield return ["~/.npmrc"];
        yield return ["~/.pypirc"];
        yield return ["~/.config/gh/hosts.yml"];
        yield return ["~/.config/nvim/init.lua"];
        yield return ["~/Downloads/cert.pem"];
        yield return ["~/Downloads/server.key"];
        yield return ["~/Downloads/cert.p12"];
        yield return ["~/Downloads/cert.pfx"];
        yield return ["~/Projects/../.ssh/id_rsa"];
        yield return ["docs/.ssh-notes.md"];
        yield return ["id_rsa"];
        yield return ["depot:cockpit"];
        yield return ["~"];
        yield return [""];
        yield return ["/etc/ssh/ssh_host_rsa_key"];
    }

    [Theory]
    [MemberData(nameof(References))]
    public void IsLikelySecretPath_AgreesOnEveryReferenceShape(string reference) =>
        Assert.Equal(HostHeuristic.IsLikelySecretPath(reference), PluginHeuristic.IsLikelySecretPath(reference));

    [Fact]
    public void IsLikelySecretPath_AgreeOnAnAbsolutePathUnderHome()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var reference = Path.Combine(home, ".ssh", "id_rsa");

        Assert.Equal(HostHeuristic.IsLikelySecretPath(reference), PluginHeuristic.IsLikelySecretPath(reference));
        Assert.True(HostHeuristic.IsLikelySecretPath(reference));
    }
}
