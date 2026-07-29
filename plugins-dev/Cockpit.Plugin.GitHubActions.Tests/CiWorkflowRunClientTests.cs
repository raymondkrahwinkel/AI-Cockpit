namespace Cockpit.Plugin.GitHubActions.Tests;

/// <summary>
/// The GitHub Actions plugin's non-UI logic (AC-52): the gh argument list, run-list JSON parsing, run-state derivation
/// and the browser-open URL guard — all without shelling out.
/// </summary>
public class CiWorkflowRunClientTests
{
    [Fact]
    public void RunListArguments_QueriesTheBranchesLatestRunAsJson()
    {
        Assert.Equal(
            ["run", "list", "--branch", "feature/AC-52", "--limit", "1",
             "--json", "workflowName,headBranch,event,status,conclusion,createdAt,url"],
            CiWorkflowRunClient.RunListArguments("feature/AC-52"));
    }

    [Fact]
    public void ParseRuns_ReadsAllFields()
    {
        const string json = """
            [{ "workflowName": "CI", "headBranch": "main", "event": "push", "status": "completed",
               "conclusion": "success", "createdAt": "2026-07-18T00:00:00Z",
               "url": "https://github.com/owner/repo/actions/runs/1" }]
            """;

        var run = Assert.Single(CiWorkflowRunClient.ParseRuns(json));

        Assert.Equal("CI", run.WorkflowName);
        Assert.Equal("main", run.Branch);
        Assert.Equal("push", run.Event);
        Assert.Equal("completed", run.Status);
        Assert.Equal("success", run.Conclusion);
        Assert.Equal("https://github.com/owner/repo/actions/runs/1", run.Url);
        Assert.Equal(CiRunState.Passed, run.State);
    }

    [Theory]
    [InlineData("completed", "success", "Passed")]
    [InlineData("completed", "failure", "Failed")]
    [InlineData("completed", "timed_out", "Failed")]
    [InlineData("completed", "cancelled", "Other")]
    [InlineData("in_progress", "", "Running")]
    [InlineData("queued", "", "Running")]
    public void State_DerivesFromStatusAndConclusion(string status, string conclusion, string expected)
    {
        Assert.Equal(expected, new CiRun("CI", "main", "push", status, conclusion, null, "https://github.com/o/r/actions/runs/1")
            .State.ToString());
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("not-json", false)]
    [InlineData("{}", false)]
    public void ParseRuns_ToleratesEmptyOrInvalidJson(string json, bool _)
    {
        Assert.Empty(CiWorkflowRunClient.ParseRuns(json));
    }

    [Theory]
    [InlineData("https://github.com/owner/repo/actions/runs/1", true)]
    [InlineData("https://api.github.com/x", true)]
    [InlineData("http://github.com/owner/repo", false)]      // not https
    [InlineData("https://github.com.evil.com/x", false)]     // look-alike host
    [InlineData("https://evil.com/github.com", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("", false)]
    public void IsGitHubRunUrl_AcceptsOnlyHttpsGitHub(string url, bool expected)
    {
        Assert.Equal(expected, CiWorkflowRunClient.IsGitHubRunUrl(url));
    }
}
