using Cockpit.Core.Plugins;
using FluentAssertions;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>Store URL auto-detection (#14): GitHub repo → raw index.json, direct .json → itself, base dir → +index.json, plus zip-path resolution.</summary>
public class PluginStoreUrlTests
{
    [Fact]
    public void TryResolveIndexUrl_GitHubRepo_ResolvesToRawIndexOnMain()
    {
        PluginStoreUrl.TryResolveIndexUrl("https://github.com/octocat/hello-world", out var indexUrl, out _).Should().BeTrue();
        indexUrl.Should().Be("https://raw.githubusercontent.com/octocat/hello-world/main/index.json");
    }

    [Fact]
    public void TryResolveIndexUrl_GitHubRepoWithBranch_UsesThatBranch()
    {
        PluginStoreUrl.TryResolveIndexUrl("https://github.com/octocat/hello-world/tree/dev", out var indexUrl, out _).Should().BeTrue();
        indexUrl.Should().Be("https://raw.githubusercontent.com/octocat/hello-world/dev/index.json");
    }

    [Fact]
    public void TryResolveIndexUrl_DirectJsonUrl_ReturnsItself()
    {
        PluginStoreUrl.TryResolveIndexUrl("https://example.com/store/index.json", out var indexUrl, out _).Should().BeTrue();
        indexUrl.Should().Be("https://example.com/store/index.json");
    }

    [Fact]
    public void TryResolveIndexUrl_BaseDirectory_AppendsIndexJson()
    {
        PluginStoreUrl.TryResolveIndexUrl("https://example.com/store", out var indexUrl, out _).Should().BeTrue();
        indexUrl.Should().Be("https://example.com/store/index.json");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("ftp://example.com/index.json")]
    public void TryResolveIndexUrl_Invalid_Rejected(string entered)
    {
        PluginStoreUrl.TryResolveIndexUrl(entered, out _, out var error).Should().BeFalse();
        error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ResolveZipUrl_RelativePath_ResolvesAgainstIndex()
    {
        var zip = PluginStoreUrl.ResolveZipUrl("https://raw.githubusercontent.com/o/r/main/index.json", "github-issues/github-issues-1.0.0.zip");
        zip.Should().Be("https://raw.githubusercontent.com/o/r/main/github-issues/github-issues-1.0.0.zip");
    }

    [Theory]
    [InlineData("https://github.com/octocat/hello-world", "octocat/hello-world")]
    [InlineData("https://github.com/octocat/hello-world/tree/dev", "octocat/hello-world")]
    [InlineData("https://raw.githubusercontent.com/octocat/hello-world/main/index.json", "octocat/hello-world")]
    [InlineData("https://plugins.example.dev/store/index.json", "plugins.example.dev")]
    [InlineData("https://plugins.example.dev/", "plugins.example.dev")]
    public void DeriveDisplayName_KnownShapes_ReadsAsOwnerRepoOrHost(string url, string expected)
    {
        PluginStoreUrl.DeriveDisplayName(url).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void DeriveDisplayName_Blank_FallsBackToPlaceholder(string url)
    {
        PluginStoreUrl.DeriveDisplayName(url).Should().Be("Unknown store");
    }

    [Fact]
    public void DeriveDisplayName_Unparseable_FallsBackToItself()
    {
        PluginStoreUrl.DeriveDisplayName("not a url").Should().Be("not a url");
    }

    [Fact]
    public void TryParseGitHubRepo_RepoUrl_ExtractsOwnerRepoAndDefaultBranch()
    {
        PluginStoreUrl.TryParseGitHubRepo("https://github.com/octocat/hello-world", out var owner, out var repo, out var branch).Should().BeTrue();
        owner.Should().Be("octocat");
        repo.Should().Be("hello-world");
        branch.Should().Be("main");
    }

    [Fact]
    public void TryParseGitHubRepo_WithBranch_UsesThatBranch()
    {
        PluginStoreUrl.TryParseGitHubRepo("https://github.com/octocat/hello-world/tree/dev", out _, out _, out var branch).Should().BeTrue();
        branch.Should().Be("dev");
    }

    [Theory]
    [InlineData("https://example.com/store/index.json")]
    [InlineData("https://raw.githubusercontent.com/o/r/main/index.json")]
    [InlineData("not a url")]
    public void TryParseGitHubRepo_NonGitHub_Rejected(string url)
    {
        PluginStoreUrl.TryParseGitHubRepo(url, out _, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void GitHubContentsUrl_BuildsAuthenticatedContentsApiUrl()
    {
        PluginStoreUrl.GitHubContentsUrl("octocat", "hello-world", "github-issues/github-issues-1.0.0.zip", "main")
            .Should().Be("https://api.github.com/repos/octocat/hello-world/contents/github-issues/github-issues-1.0.0.zip?ref=main");
    }

    [Fact]
    public void GitHubContentsUrl_EncodesEachSegment_SoAPathCannotInjectAQuery()
    {
        PluginStoreUrl.GitHubContentsUrl("octocat", "hello-world", "dir/a b.zip?ref=evil", "main")
            .Should().Be("https://api.github.com/repos/octocat/hello-world/contents/dir/a%20b.zip%3Fref%3Devil?ref=main");
    }

    [Theory]
    [InlineData("github-issues/github-issues-1.0.0.zip")]
    [InlineData("plugin.zip")]
    public void IsSafeRelativePath_PlainRelativePath_IsSafe(string path)
    {
        PluginStoreUrl.IsSafeRelativePath(path).Should().BeTrue();
    }

    [Theory]
    [InlineData("../../other/repo/secret.zip")]
    [InlineData("a/../../b.zip")]
    [InlineData("//evil.example/x.zip")]
    [InlineData("https://evil.example/x.zip")]
    [InlineData("")]
    public void IsSafeRelativePath_EscapingOrAbsolutePath_IsUnsafe(string path)
    {
        PluginStoreUrl.IsSafeRelativePath(path).Should().BeFalse();
    }
}
