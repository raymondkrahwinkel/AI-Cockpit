
namespace Cockpit.Plugin.GitHubIssues.Tests;

// The GitHub Issues Autopilot code templates (AC-216): "Bug fix" and "Feature" are code runs that must end with a
// merge-ready pull request, so each carries the PR-delivery signal and a brief telling the agents to commit and push —
// with commits kept clean of any Co-Authored-By trailer or AI/agent mention (a hard project rule).
public class GitHubAutopilotTemplatesTests
{
    [Theory]
    [InlineData("github-issues.bugfix")]
    [InlineData("github-issues.feature")]
    public void CodeTemplates_DeliverAPullRequest(string id)
    {
        var template = GitHubAutopilotTemplates.All.Single(t => t.Id == id);
        Assert.True(template.DeliversPullRequest);
    }

    [Theory]
    [InlineData("github-issues.bugfix")]
    [InlineData("github-issues.feature")]
    public void CodeTemplates_TellTheAgentToCommitPushAndOpenAPr(string id)
    {
        var body = GitHubAutopilotTemplates.All.Single(t => t.Id == id).Body.ToLowerInvariant();
        Assert.Contains("commit", body);
        Assert.Contains("push", body);
        Assert.Contains("pull request", body);
    }

    [Theory]
    [InlineData("github-issues.bugfix")]
    [InlineData("github-issues.feature")]
    public void CodeTemplates_ForbidCoAuthorAndAiMentionsInCommits(string id)
    {
        var body = GitHubAutopilotTemplates.All.Single(t => t.Id == id).Body;
        Assert.Contains("Co-Authored-By", body);
        Assert.Contains("no mention of an ai", body.ToLowerInvariant());
    }
}
