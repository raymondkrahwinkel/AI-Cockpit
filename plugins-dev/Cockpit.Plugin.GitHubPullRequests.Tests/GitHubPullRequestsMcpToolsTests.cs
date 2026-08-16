namespace Cockpit.Plugin.GitHubPullRequests.Tests;

// AC-818: proves concurrent callers for the same PR share one in-flight fetch. Asserted via Task identity so
// it does not depend on `gh` being installed/authenticated in CI.
public class GitHubPullRequestsMcpToolsTests
{
    [Fact]
    public async Task GetPrStatus_ConcurrentCallsForTheSamePr_ShareTheSameInFlightFetch()
    {
        var tools = new GitHubPullRequestsMcpTools(new GitHubPrGhClient());

        var first = tools.GetPrStatus("ac818-owner", "ac818-repo", 1);
        var second = tools.GetPrStatus("ac818-owner", "ac818-repo", 1);

        Assert.Same(first, second);
        await Task.WhenAll(first, second);
    }

    [Fact]
    public async Task GetPrStatus_DifferentPullRequests_GetSeparateFetches()
    {
        var tools = new GitHubPullRequestsMcpTools(new GitHubPrGhClient());

        var a = tools.GetPrStatus("ac818-owner", "ac818-repo", 2);
        var b = tools.GetPrStatus("ac818-owner", "ac818-repo", 3);

        Assert.NotSame(a, b);
        await Task.WhenAll(a, b);
    }
}
