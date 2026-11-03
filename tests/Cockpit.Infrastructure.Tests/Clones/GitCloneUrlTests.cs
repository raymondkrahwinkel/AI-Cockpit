using Cockpit.Infrastructure.Clones;

namespace Cockpit.Infrastructure.Tests.Clones;

/// <summary>
/// The pure git-URL parsing behind clone-from-URL (AC-90): the slug a clone lives under, the URL git is handed, and
/// the identity two remotes are de-duplicated by. Kept off the filesystem so the fiddly cases — scp-style SSH,
/// nested groups, a trailing <c>.git</c>, and above all credentials in an HTTPS URL — are pinned on their own.
/// </summary>
public sealed class GitCloneUrlTests
{
    [Fact]
    public void Parse_CommonHttpsAndScpStyleUrls_KeepTheirRequiredParts()
    {
        var parsed = GitCloneUrl.Parse("https://gitlab.com/group/subgroup/repo.git");

        Assert.Equal("gitlab.com", parsed.Host);
        Assert.Equal(new[] { "group", "subgroup", "repo" }, parsed.Segments);
        Assert.Equal("gitlab.com/group/subgroup/repo", parsed.Slug);
        Assert.Equal("https://gitlab.com/group/subgroup/repo", parsed.RemoteUrl);
        Assert.Equal("https://ghe.example.com:8443/org/repo", GitCloneUrl.Parse("https://ghe.example.com:8443/org/repo.git").RemoteUrl);
        var ssh = GitCloneUrl.Parse("git@github.com:org/repo.git");
        Assert.Equal("github.com", ssh.Host);
        Assert.Equal(new[] { "org", "repo" }, ssh.Segments);
        Assert.Equal("git@github.com:org/repo.git", ssh.RemoteUrl);
        // Credentials never reach git's argv or config; ssh keeps its required login, HTTPS drops all userinfo.
        var httpsWithCredential = GitCloneUrl.Parse("https://x-access-token:ghp_secretsecret@github.com/org/repo.git");
        Assert.Equal("https://github.com/org/repo", httpsWithCredential.RemoteUrl);
        Assert.DoesNotContain("ghp_secretsecret", httpsWithCredential.RemoteUrl);
        Assert.DoesNotContain("@", httpsWithCredential.RemoteUrl);
        var sshWithCredential = GitCloneUrl.Parse("ssh://git:s3cr3t-token@github.com/org/repo.git");
        Assert.Equal("ssh://git@github.com/org/repo.git", sshWithCredential.RemoteUrl);
        Assert.DoesNotContain("s3cr3t-token", sshWithCredential.RemoteUrl);
        Assert.Equal("github.com", sshWithCredential.Host);
        Assert.Equal("github.com/org/repo", sshWithCredential.Slug);
    }

    [Fact]
    public void Parse_TraversalInPath_IsSanitizedAwayRatherThanEscapingTheRoot()
    {
        // A pasted "..“ segment must never become a real parent-directory hop in the managed clones root.
        Assert.DoesNotContain("..", GitCloneUrl.Parse("https://github.com/../../etc/repo.git").Segments);
    }
}
