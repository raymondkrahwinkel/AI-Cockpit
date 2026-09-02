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
    public void Parse_HttpsUrl_ExtractsHostSlugAndSegments()
    {
        var parsed = GitCloneUrl.Parse("https://github.com/org/repo.git");

        Assert.Equal("github.com", parsed.Host);
        Assert.Equal(new[] { "org", "repo" }, parsed.Segments);
        Assert.Equal("github.com/org/repo", parsed.Slug);
        Assert.Equal("https://github.com/org/repo", parsed.RemoteUrl);
    }

    // The load-bearing security property (a binding rule): a token in an HTTPS URL is dropped before git ever sees
    // it, so it cannot land in argv, .git/config or a log — the clone falls back to the host credential helper.
    [Fact]
    public void Parse_HttpsUrlWithCredentials_StripsThemFromTheUrlGitReceives()
    {
        var parsed = GitCloneUrl.Parse("https://x-access-token:ghp_secretsecret@github.com/org/repo.git");

        Assert.Equal("https://github.com/org/repo", parsed.RemoteUrl);
        Assert.DoesNotContain("ghp_secretsecret", parsed.RemoteUrl);
        Assert.DoesNotContain("@", parsed.RemoteUrl);
    }

    [Fact]
    public void Parse_HttpsUrlWithNonDefaultPort_KeepsThePort()
    {
        Assert.Equal("https://ghe.example.com:8443/org/repo", GitCloneUrl.Parse("https://ghe.example.com:8443/org/repo.git").RemoteUrl);
    }

    [Fact]
    public void Parse_ScpStyleSshUrl_KeepsTheLoginAndParsesHostAndPath()
    {
        var parsed = GitCloneUrl.Parse("git@github.com:org/repo.git");

        Assert.Equal("github.com", parsed.Host);
        Assert.Equal(new[] { "org", "repo" }, parsed.Segments);
        // The git@ user is the SSH login, not a secret, and the clone needs it — kept verbatim.
        Assert.Equal("git@github.com:org/repo.git", parsed.RemoteUrl);
    }

    [Fact]
    public void Parse_SshSchemeUrl_ExtractsHostAndPath()
    {
        var parsed = GitCloneUrl.Parse("ssh://git@github.com/org/repo.git");

        Assert.Equal("github.com", parsed.Host);
        Assert.Equal("github.com/org/repo", parsed.Slug);
        Assert.Equal("ssh://git@github.com/org/repo.git", parsed.RemoteUrl);
    }

    // The same binding rule as HTTPS, for the one scheme that kept its userinfo: an ssh:// URL may carry the git@
    // login (needed, not a secret), but a password after it must not reach argv, .git/config or the registry. The
    // login is kept; only the password is cut. The repository path is left exactly as given.
    [Fact]
    public void Parse_SshSchemeUrlWithPassword_StripsThePasswordButKeepsTheLoginAndPath()
    {
        var parsed = GitCloneUrl.Parse("ssh://git:s3cr3t-token@github.com/org/repo.git");

        Assert.Equal("ssh://git@github.com/org/repo.git", parsed.RemoteUrl);
        Assert.DoesNotContain("s3cr3t-token", parsed.RemoteUrl);
    }

    [Fact]
    public void Parse_NestedGroup_KeepsEverySegment()
    {
        Assert.Equal("gitlab.com/group/subgroup/repo", GitCloneUrl.Parse("https://gitlab.com/group/subgroup/repo.git").Slug);
    }

    [Fact]
    public void SameRepositoryAs_HttpsAndScpForTheSameRepo_Match()
    {
        Assert.True(GitCloneUrl.Parse("https://github.com/org/repo.git")
            .SameRepositoryAs("git@github.com:org/repo.git"));
    }

    [Fact]
    public void SameRepositoryAs_DiffersOnlyByCase_StillMatches()
    {
        // GitHub treats org/repo case-insensitively; the slug is lowercased so the same repository is not cloned
        // twice under two folders.
        Assert.True(GitCloneUrl.Parse("https://github.com/Org/Repo.git")
            .SameRepositoryAs("https://github.com/org/repo"));
    }

    [Fact]
    public void SameRepositoryAs_DifferentRepository_DoesNotMatch()
    {
        Assert.False(GitCloneUrl.Parse("https://github.com/org/repo.git")
            .SameRepositoryAs("https://github.com/org/other.git"));
    }

    [Fact]
    public void SameRepositoryAs_UnparseableRemote_IsTreatedAsNotMatching()
    {
        Assert.False(GitCloneUrl.Parse("https://github.com/org/repo.git")
            .SameRepositoryAs("not a url"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://github.com")]
    [InlineData("https://github.com/")]
    [InlineData("not-a-url")]
    public void Parse_InputThatNamesNoRepository_Throws(string url)
    {
        var act = () => GitCloneUrl.Parse(url);

        Assert.Throws<FormatException>(act);
    }

    [Fact]
    public void Parse_TraversalInPath_IsSanitizedAwayRatherThanEscapingTheRoot()
    {
        // A pasted "..“ segment must never become a real parent-directory hop in the managed clones root.
        Assert.DoesNotContain("..", GitCloneUrl.Parse("https://github.com/../../etc/repo.git").Segments);
    }
}
