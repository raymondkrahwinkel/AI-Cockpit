using FluentAssertions;

namespace Cockpit.Plugin.GitHubIssues.Tests;

/// <summary>
/// The GitHub Issues Autopilot code templates (AC-216): "Bug fix" and "Feature" are code runs that must end with a
/// merge-ready pull request, so each carries the PR-delivery signal and a brief telling the agents to commit and push —
/// with commits kept clean of any Co-Authored-By trailer or AI/agent mention (a hard project rule).
/// </summary>
public class GitHubAutopilotTemplatesTests
{
    [Theory]
    [InlineData("github-issues.bugfix")]
    [InlineData("github-issues.feature")]
    public void CodeTemplates_DeliverAPullRequest(string id)
    {
        var template = GitHubAutopilotTemplates.All.Single(t => t.Id == id);
        template.DeliversPullRequest.Should().BeTrue();
    }

    [Theory]
    [InlineData("github-issues.bugfix")]
    [InlineData("github-issues.feature")]
    public void CodeTemplates_TellTheAgentToCommitPushAndOpenAPr(string id)
    {
        var body = GitHubAutopilotTemplates.All.Single(t => t.Id == id).Body.ToLowerInvariant();
        body.Should().Contain("commit");
        body.Should().Contain("push");
        body.Should().Contain("pull request");
    }

    [Theory]
    [InlineData("github-issues.bugfix")]
    [InlineData("github-issues.feature")]
    public void CodeTemplates_ForbidCoAuthorAndAiMentionsInCommits(string id)
    {
        var body = GitHubAutopilotTemplates.All.Single(t => t.Id == id).Body;
        body.Should().Contain("Co-Authored-By");
        body.ToLowerInvariant().Should().Contain("no mention of an ai");
    }
}
