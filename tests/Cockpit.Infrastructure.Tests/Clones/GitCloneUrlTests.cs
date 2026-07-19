using Cockpit.Infrastructure.Clones;
using FluentAssertions;

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

        parsed.Host.Should().Be("github.com");
        parsed.Segments.Should().Equal("org", "repo");
        parsed.Slug.Should().Be("github.com/org/repo");
        parsed.RemoteUrl.Should().Be("https://github.com/org/repo");
    }

    [Fact]
    public void Parse_HttpsUrlWithoutGitSuffix_IsEquivalentToWithIt()
    {
        GitCloneUrl.Parse("https://github.com/org/repo").NormalizedKey
            .Should().Be(GitCloneUrl.Parse("https://github.com/org/repo.git").NormalizedKey);
    }

    // The load-bearing security property (a binding rule): a token in an HTTPS URL is dropped before git ever sees
    // it, so it cannot land in argv, .git/config or a log — the clone falls back to the host credential helper.
    [Fact]
    public void Parse_HttpsUrlWithCredentials_StripsThemFromTheUrlGitReceives()
    {
        var parsed = GitCloneUrl.Parse("https://x-access-token:ghp_secretsecret@github.com/org/repo.git");

        parsed.RemoteUrl.Should().Be("https://github.com/org/repo");
        parsed.RemoteUrl.Should().NotContain("ghp_secretsecret");
        parsed.RemoteUrl.Should().NotContain("@");
    }

    [Fact]
    public void Parse_HttpsUrlWithNonDefaultPort_KeepsThePort()
    {
        GitCloneUrl.Parse("https://ghe.example.com:8443/org/repo.git").RemoteUrl
            .Should().Be("https://ghe.example.com:8443/org/repo");
    }

    [Fact]
    public void Parse_ScpStyleSshUrl_KeepsTheLoginAndParsesHostAndPath()
    {
        var parsed = GitCloneUrl.Parse("git@github.com:org/repo.git");

        parsed.Host.Should().Be("github.com");
        parsed.Segments.Should().Equal("org", "repo");
        // The git@ user is the SSH login, not a secret, and the clone needs it — kept verbatim.
        parsed.RemoteUrl.Should().Be("git@github.com:org/repo.git");
    }

    [Fact]
    public void Parse_SshSchemeUrl_ExtractsHostAndPath()
    {
        var parsed = GitCloneUrl.Parse("ssh://git@github.com/org/repo.git");

        parsed.Host.Should().Be("github.com");
        parsed.Slug.Should().Be("github.com/org/repo");
        parsed.RemoteUrl.Should().Be("ssh://git@github.com/org/repo.git");
    }

    [Fact]
    public void Parse_NestedGroup_KeepsEverySegment()
    {
        GitCloneUrl.Parse("https://gitlab.com/group/subgroup/repo.git").Slug
            .Should().Be("gitlab.com/group/subgroup/repo");
    }

    [Fact]
    public void SameRepositoryAs_HttpsAndScpForTheSameRepo_Match()
    {
        GitCloneUrl.Parse("https://github.com/org/repo.git")
            .SameRepositoryAs("git@github.com:org/repo.git")
            .Should().BeTrue();
    }

    [Fact]
    public void SameRepositoryAs_DiffersOnlyByCase_StillMatches()
    {
        // GitHub treats org/repo case-insensitively; the slug is lowercased so the same repository is not cloned
        // twice under two folders.
        GitCloneUrl.Parse("https://github.com/Org/Repo.git")
            .SameRepositoryAs("https://github.com/org/repo")
            .Should().BeTrue();
    }

    [Fact]
    public void SameRepositoryAs_DifferentRepository_DoesNotMatch()
    {
        GitCloneUrl.Parse("https://github.com/org/repo.git")
            .SameRepositoryAs("https://github.com/org/other.git")
            .Should().BeFalse();
    }

    [Fact]
    public void SameRepositoryAs_UnparseableRemote_IsTreatedAsNotMatching()
    {
        GitCloneUrl.Parse("https://github.com/org/repo.git")
            .SameRepositoryAs("not a url")
            .Should().BeFalse();
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

        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Parse_TraversalInPath_IsSanitizedAwayRatherThanEscapingTheRoot()
    {
        // A pasted "..“ segment must never become a real parent-directory hop in the managed clones root.
        GitCloneUrl.Parse("https://github.com/../../etc/repo.git").Segments
            .Should().NotContain("..");
    }
}
