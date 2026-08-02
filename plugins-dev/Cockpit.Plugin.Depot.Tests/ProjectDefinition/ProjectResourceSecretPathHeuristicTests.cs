using Cockpit.Plugin.Depot.ProjectDefinition;

namespace Cockpit.Plugin.Depot.Tests.ProjectDefinition;

// AC-612: pins this plugin's own mirrored `ProjectResourceSecretPathHeuristic` in isolation — the same
// table `Cockpit.Core.Tests.Projects.ProjectResourceSecretPathHeuristicTests` pins on the host side.
// `ProjectResourceSecretPathParityTests` is what actually guarantees the two agree; this file exists so
// a change made only here (this plugin cannot reference `Cockpit.Core`, so the two copies can never share one
// test file) still has its own red-without-fix coverage.
public class ProjectResourceSecretPathHeuristicTests
{
    [Theory]
    [InlineData("~/.ssh/id_rsa", true)]
    [InlineData("~/.ssh/id_rsa.pub", false)]
    [InlineData("~/.ssh/known_hosts", true)]
    [InlineData("~/.sshfoo/x", false)]
    [InlineData("~/.gnupg/private-keys-v1.d/foo", true)]
    [InlineData("~/.aws", true)]
    [InlineData("~/.aws/credentials", true)]
    [InlineData("~/.kube/config", true)]
    [InlineData("~/.config/gh/hosts.yml", true)]
    [InlineData("~/.config/nvim/init.lua", false)]
    [InlineData("~/.docker/config.json", true)]
    [InlineData("~/.docker/daemon.json", false)]
    [InlineData("~/.netrc", true)]
    [InlineData("~/.npmrc", true)]
    [InlineData("~/.pypirc", true)]
    [InlineData("~/Downloads/server.pem", true)]
    [InlineData("~/Downloads/server.key", true)]
    [InlineData("~/Downloads/cert.p12", true)]
    [InlineData("~/Downloads/cert.pfx", true)]
    [InlineData("~/Downloads/identity.txt", false)]
    [InlineData("~/Projects/../.ssh/id_rsa", true)]
    [InlineData("docs/.ssh-notes.md", false)]
    [InlineData("id_rsa", false)]
    [InlineData("depot:cockpit", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("~", false)]
    public void IsLikelySecretPath_MatchesTheMeasuredTable(string? reference, bool expected) =>
        Assert.Equal(expected, ProjectResourceSecretPathHeuristic.IsLikelySecretPath(reference));

    [Fact]
    public void IsLikelySecretPath_AnAbsolutePathUnderHome_MatchesItsAnchorFormsAnswer()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var absolute = Path.Combine(home, ".ssh", "id_rsa");

        Assert.True(ProjectResourceSecretPathHeuristic.IsLikelySecretPath(absolute));
    }

    [Fact]
    public void IsLikelySecretPath_UppercasedDirectory_MatchesOnlyOnAPlatformThatIgnoresCase() =>
        Assert.Equal(OperatingSystem.IsWindows(), ProjectResourceSecretPathHeuristic.IsLikelySecretPath("~/.SSH/ID_RSA"));
}
