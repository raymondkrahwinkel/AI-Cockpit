
namespace Cockpit.Plugin.YouTrack.Tests;

/// <summary>
/// The Autopilot templates this plugin contributes (AC-189/AC-216/AC-217): bug fix, feature, and the epic template that
/// plans a YouTrack epic — an issue whose real work is its "parent for" child issues — as one coherent run to one PR.
/// The code templates (bug fix, feature, epic) each carry the PR-delivery signal and, for bug fix/feature, a brief telling
/// the agents to commit and push with commits kept clean of any Co-Authored-By trailer or AI/agent mention (a hard
/// project rule). Asserted on the pure registration so the epic instruction (fetch the children via the links, one plan,
/// one PR) and the PR-delivery signal do not drift.
/// </summary>
public class YouTrackAutopilotTemplatesTests
{
    [Fact]
    public void All_RegistersBugFixFeatureAndEpic()
    {
        Assert.Equal(new[] { "youtrack.bugfix", "youtrack.feature", "youtrack.epic" }, YouTrackAutopilotTemplates.All.Select(template => template.Id));
    }

    [Fact]
    public void Epic_InstructsTheCeoToPullChildIssues_PlanAsOneRun_AndLandOnePr()
    {
        var epic = YouTrackAutopilotTemplates.All.Single(template => template.Id == "youtrack.epic");

        Assert.Equal("Epic", epic.Name);
        // Fetch the sub-items from the "parent for" child links (AC-158's shape), not from the description.
        Assert.Contains("parent for", epic.Body);
        Assert.Contains("child issues", epic.Body);
        Assert.Contains("read tools", epic.Body);
        Assert.Contains("not the description", epic.Body);
        // One coherent run to one PR, with the ids recorded so the PR names what it closes.
        Assert.Contains("ONE plan", epic.Body);
        Assert.Contains("ONE pull request", epic.Body);
        Assert.Contains("{{issue.id}}", epic.Body);
        // Carries the issue placeholders Autopilot fills at run time.
        Assert.Equal(new[] { "issue.id", "issue.title" }, epic.RequiredPlaceholders);
    }

    [Theory]
    [InlineData("youtrack.bugfix")]
    [InlineData("youtrack.feature")]
    [InlineData("youtrack.epic")]
    public void CodeTemplates_DeliverAPullRequest(string id)
    {
        // An epic is a code run too: it accumulates its children on one branch and lands one merge-ready PR, so the
        // AC-216 finalizer must fire for it — the signal is stamped on the registration, not only on bug fix/feature.
        var template = YouTrackAutopilotTemplates.All.Single(t => t.Id == id);
        Assert.True(template.DeliversPullRequest);
    }

    [Theory]
    [InlineData("youtrack.bugfix")]
    [InlineData("youtrack.feature")]
    public void CodeTemplates_TellTheAgentToCommitPushAndOpenAPr(string id)
    {
        var body = YouTrackAutopilotTemplates.All.Single(t => t.Id == id).Body.ToLowerInvariant();
        Assert.Contains("commit", body);
        Assert.Contains("push", body);
        Assert.Contains("pull request", body);
    }

    [Theory]
    [InlineData("youtrack.bugfix")]
    [InlineData("youtrack.feature")]
    public void CodeTemplates_ForbidCoAuthorAndAiMentionsInCommits(string id)
    {
        var body = YouTrackAutopilotTemplates.All.Single(t => t.Id == id).Body;
        Assert.Contains("Co-Authored-By", body);
        Assert.Contains("no mention of an ai", body.ToLowerInvariant());
    }
}
