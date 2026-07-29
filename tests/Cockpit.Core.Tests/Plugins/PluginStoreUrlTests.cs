using Cockpit.Core.Plugins;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>Store URL auto-detection (#14): GitHub repo → raw index.json, direct .json → itself, base dir → +index.json, plus zip-path resolution.</summary>
public class PluginStoreUrlTests
{
    [Fact]
    public void TryResolveIndexUrl_GitHubRepo_ResolvesToRawIndexOnMain()
    {
        Assert.True(PluginStoreUrl.TryResolveIndexUrl("https://github.com/octocat/hello-world", out var indexUrl, out _));
        Assert.Equal("https://raw.githubusercontent.com/octocat/hello-world/main/index.json", indexUrl);
    }

    [Fact]
    public void TryResolveIndexUrl_GitHubRepoWithBranch_UsesThatBranch()
    {
        Assert.True(PluginStoreUrl.TryResolveIndexUrl("https://github.com/octocat/hello-world/tree/dev", out var indexUrl, out _));
        Assert.Equal("https://raw.githubusercontent.com/octocat/hello-world/dev/index.json", indexUrl);
    }

    [Fact]
    public void TryResolveIndexUrl_DirectJsonUrl_ReturnsItself()
    {
        Assert.True(PluginStoreUrl.TryResolveIndexUrl("https://example.com/store/index.json", out var indexUrl, out _));
        Assert.Equal("https://example.com/store/index.json", indexUrl);
    }

    [Fact]
    public void TryResolveIndexUrl_BaseDirectory_AppendsIndexJson()
    {
        Assert.True(PluginStoreUrl.TryResolveIndexUrl("https://example.com/store", out var indexUrl, out _));
        Assert.Equal("https://example.com/store/index.json", indexUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("ftp://example.com/index.json")]
    public void TryResolveIndexUrl_Invalid_Rejected(string entered)
    {
        Assert.False(PluginStoreUrl.TryResolveIndexUrl(entered, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void ResolveZipUrl_RelativePath_ResolvesAgainstIndex()
    {
        var zip = PluginStoreUrl.ResolveZipUrl("https://raw.githubusercontent.com/o/r/main/index.json", "github-issues/github-issues-1.0.0.zip");
        Assert.Equal("https://raw.githubusercontent.com/o/r/main/github-issues/github-issues-1.0.0.zip", zip);
    }

    [Theory]
    [InlineData("https://github.com/octocat/hello-world", "octocat/hello-world")]
    [InlineData("https://github.com/octocat/hello-world/tree/dev", "octocat/hello-world")]
    [InlineData("https://raw.githubusercontent.com/octocat/hello-world/main/index.json", "octocat/hello-world")]
    [InlineData("https://plugins.example.dev/store/index.json", "plugins.example.dev")]
    [InlineData("https://plugins.example.dev/", "plugins.example.dev")]
    public void DeriveDisplayName_KnownShapes_ReadsAsOwnerRepoOrHost(string url, string expected)
    {
        Assert.Equal(expected, PluginStoreUrl.DeriveDisplayName(url));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void DeriveDisplayName_Blank_FallsBackToPlaceholder(string url)
    {
        Assert.Equal("Unknown store", PluginStoreUrl.DeriveDisplayName(url));
    }

    [Fact]
    public void DeriveDisplayName_Unparseable_FallsBackToItself()
    {
        Assert.Equal("not a url", PluginStoreUrl.DeriveDisplayName("not a url"));
    }

    [Fact]
    public void TryParseGitHubRepo_RepoUrl_ExtractsOwnerRepoAndDefaultBranch()
    {
        Assert.True(PluginStoreUrl.TryParseGitHubRepo("https://github.com/octocat/hello-world", out var owner, out var repo, out var branch));
        Assert.Equal("octocat", owner);
        Assert.Equal("hello-world", repo);
        Assert.Equal("main", branch);
    }

    [Fact]
    public void TryParseGitHubRepo_WithBranch_UsesThatBranch()
    {
        Assert.True(PluginStoreUrl.TryParseGitHubRepo("https://github.com/octocat/hello-world/tree/dev", out _, out _, out var branch));
        Assert.Equal("dev", branch);
    }

    [Theory]
    [InlineData("https://example.com/store/index.json")]
    [InlineData("https://raw.githubusercontent.com/o/r/main/index.json")]
    [InlineData("not a url")]
    public void TryParseGitHubRepo_NonGitHub_Rejected(string url)
    {
        Assert.False(PluginStoreUrl.TryParseGitHubRepo(url, out _, out _, out _));
    }

    [Fact]
    public void GitHubContentsUrl_BuildsAuthenticatedContentsApiUrl()
    {
        Assert.Equal(
            "https://api.github.com/repos/octocat/hello-world/contents/github-issues/github-issues-1.0.0.zip?ref=main",
            PluginStoreUrl.GitHubContentsUrl("octocat", "hello-world", "github-issues/github-issues-1.0.0.zip", "main"));
    }

    [Fact]
    public void GitHubContentsUrl_EncodesEachSegment_SoAPathCannotInjectAQuery()
    {
        Assert.Equal(
            "https://api.github.com/repos/octocat/hello-world/contents/dir/a%20b.zip%3Fref%3Devil?ref=main",
            PluginStoreUrl.GitHubContentsUrl("octocat", "hello-world", "dir/a b.zip?ref=evil", "main"));
    }

    [Theory]
    [InlineData("github-issues/github-issues-1.0.0.zip")]
    [InlineData("plugin.zip")]
    public void IsSafeRelativePath_PlainRelativePath_IsSafe(string path)
    {
        Assert.True(PluginStoreUrl.IsSafeRelativePath(path));
    }

    [Theory]
    [InlineData("../../other/repo/secret.zip")]
    [InlineData("a/../../b.zip")]
    [InlineData("//evil.example/x.zip")]
    [InlineData("https://evil.example/x.zip")]
    [InlineData("")]
    public void IsSafeRelativePath_EscapingOrAbsolutePath_IsUnsafe(string path)
    {
        Assert.False(PluginStoreUrl.IsSafeRelativePath(path));
    }
}
