using Cockpit.Plugin.GitHubPullRequests.Contracts;

namespace Cockpit.Plugin.GitHubPullRequests.Tests;

// AC-802: the session banner's non-UI logic, proven without shelling out — same approach as CiWorkflowRunClientTests.
public class SessionPullRequestStatusClientTests
{

    [Fact]
    public void Parse_ReadsCheckRunsByStatusAndConclusion_AndStatusContextsByStateAlone()
    {
        const string json = """
            { "number": 592, "headRefName": "main", "additions": 0, "deletions": 0,
              "url": "https://github.com/o/r/pull/592",
              "statusCheckRollup": [
                { "__typename": "CheckRun", "name": "build", "status": "COMPLETED", "conclusion": "SUCCESS",
                  "startedAt": "2026-08-15T10:00:00Z", "completedAt": "2026-08-15T10:01:42Z" },
                { "__typename": "CheckRun", "name": "tests", "status": "COMPLETED", "conclusion": "FAILURE",
                  "startedAt": "2026-08-15T10:00:00Z", "completedAt": "2026-08-15T10:03:08Z" },
                { "__typename": "CheckRun", "name": "plugins", "status": "IN_PROGRESS", "conclusion": null },
                { "__typename": "StatusContext", "context": "ci/external", "state": "SUCCESS" },
                { "__typename": "StatusContext", "context": "ci/other", "state": "PENDING" }
              ]
            }
            """;

        var status = SessionPullRequestStatusClient.Parse(json);

        Assert.NotNull(status);
        Assert.Equal(5, status!.Checks.Count);
        Assert.Equal(("build", PullRequestCheckState.Passed, TimeSpan.FromSeconds(102)), _Tuple(status.Checks[0]));
        Assert.Equal(("tests", PullRequestCheckState.Failed, TimeSpan.FromSeconds(188)), _Tuple(status.Checks[1]));
        Assert.Equal(("plugins", PullRequestCheckState.Running, (TimeSpan?)null), _Tuple(status.Checks[2]));

        // A StatusContext entry carries no status/conclusion pair: its state alone decides.
        Assert.Equal(("ci/external", PullRequestCheckState.Passed), (status.Checks[3].Name, status.Checks[3].State));
        Assert.Equal(("ci/other", PullRequestCheckState.Running), (status.Checks[4].Name, status.Checks[4].State));
    }

    private static (string Name, PullRequestCheckState State, TimeSpan? Duration) _Tuple(PullRequestCheck check) =>
        (check.Name, check.State, check.Duration);
}
