using Cockpit.Plugin.GitHubPullRequests.Contracts;

namespace Cockpit.Plugin.GitHubPullRequests.Tests;

// AC-802: the session banner's non-UI logic, proven without shelling out — same approach as CiWorkflowRunClientTests.
public class SessionPullRequestStatusClientTests
{

    [Fact]
    public void Parse_ReadsCheckRunEntries_ByStatusAndConclusion()
    {
        const string json = """
            { "number": 592, "headRefName": "main", "additions": 0, "deletions": 0,
              "url": "https://github.com/o/r/pull/592",
              "statusCheckRollup": [
                { "__typename": "CheckRun", "name": "build", "status": "COMPLETED", "conclusion": "SUCCESS",
                  "startedAt": "2026-08-15T10:00:00Z", "completedAt": "2026-08-15T10:01:42Z" },
                { "__typename": "CheckRun", "name": "tests", "status": "COMPLETED", "conclusion": "FAILURE",
                  "startedAt": "2026-08-15T10:00:00Z", "completedAt": "2026-08-15T10:03:08Z" },
                { "__typename": "CheckRun", "name": "plugins", "status": "IN_PROGRESS", "conclusion": null }
              ]
            }
            """;

        var status = SessionPullRequestStatusClient.Parse(json);

        Assert.NotNull(status);
        Assert.Equal(3, status!.Checks.Count);
        Assert.Equal(("build", PullRequestCheckState.Passed, TimeSpan.FromSeconds(102)), _Tuple(status.Checks[0]));
        Assert.Equal(("tests", PullRequestCheckState.Failed, TimeSpan.FromSeconds(188)), _Tuple(status.Checks[1]));
        Assert.Equal(("plugins", PullRequestCheckState.Running, (TimeSpan?)null), _Tuple(status.Checks[2]));
    }

    [Fact]
    public void Parse_ReadsStatusContextEntries_ByStateAlone()
    {
        const string json = """
            { "number": 1, "headRefName": "main", "additions": 0, "deletions": 0, "url": "https://github.com/o/r/pull/1",
              "statusCheckRollup": [
                { "__typename": "StatusContext", "context": "ci/external", "state": "SUCCESS" },
                { "__typename": "StatusContext", "context": "ci/other", "state": "PENDING" }
              ]
            }
            """;

        var status = SessionPullRequestStatusClient.Parse(json);

        Assert.NotNull(status);
        Assert.Equal("ci/external", status!.Checks[0].Name);
        Assert.Equal(PullRequestCheckState.Passed, status.Checks[0].State);
        Assert.Equal("ci/other", status.Checks[1].Name);
        Assert.Equal(PullRequestCheckState.Running, status.Checks[1].State);
    }

    private static (string Name, PullRequestCheckState State, TimeSpan? Duration) _Tuple(PullRequestCheck check) =>
        (check.Name, check.State, check.Duration);
}
