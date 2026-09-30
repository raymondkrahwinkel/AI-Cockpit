using Cockpit.Core.Plugins;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>Store URL auto-detection (#14): GitHub repo → raw index.json, direct .json → itself, base dir → +index.json, plus zip-path resolution.</summary>
public class PluginStoreUrlTests
{
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

}
