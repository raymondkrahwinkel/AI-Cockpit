namespace Cockpit.Plugin.GitHubPullRequests.Tests;

// The session banner's non-UI logic (AC-802): the gh argument list, `gh pr view --json …` parsing (both
// statusCheckRollup shapes — a CheckRun from GitHub Actions and a legacy StatusContext), state derivation and the
// visibility rule (AC-6: no open PR / invalid output means null, never a thrown error) — all without shelling out,
// mirroring how CiWorkflowRunClientTests proves the sibling GitHubActions plugin's own parsing.
public class SessionPullRequestStatusClientTests
{
    [Fact]
    public void ViewArguments_QueriesTheCurrentCheckoutsOpenPrAsJson()
    {
        Assert.Equal(
            ["pr", "view", "--json", "number,headRefName,additions,deletions,url,statusCheckRollup"],
            SessionPullRequestStatusClient.ViewArguments);
    }

    [Fact]
    public void Parse_ReadsThePrFieldsAndTheRepositoryFromTheUrl()
    {
        const string json = """
            { "number": 592, "headRefName": "cockpit/work-596f55f3", "additions": 248, "deletions": 31,
              "url": "https://github.com/Synvolution/cockpit/pull/592", "statusCheckRollup": [] }
            """;

        var status = SessionPullRequestStatusClient.Parse(json);

        Assert.NotNull(status);
        Assert.Equal(592, status!.Number);
        Assert.Equal("Synvolution/cockpit", status.Repository);
        Assert.Equal("cockpit/work-596f55f3", status.Branch);
        Assert.Equal(248, status.Additions);
        Assert.Equal(31, status.Deletions);
        Assert.Equal("https://github.com/Synvolution/cockpit/pull/592", status.Url);
        Assert.Empty(status.Checks);
    }

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

    [Theory]
    [InlineData("COMPLETED", "SUCCESS", "Passed")]
    [InlineData("COMPLETED", "FAILURE", "Failed")]
    [InlineData("COMPLETED", "TIMED_OUT", "Failed")]
    [InlineData("COMPLETED", "CANCELLED", "Other")]
    [InlineData("COMPLETED", "NEUTRAL", "Other")]
    [InlineData("COMPLETED", "SKIPPED", "Other")]
    [InlineData("IN_PROGRESS", null, "Running")]
    [InlineData("QUEUED", null, "Running")]
    public void CheckRunState_DerivesFromStatusAndConclusion(string status, string? conclusion, string expected)
    {
        var conclusionJson = conclusion is null ? "null" : $"\"{conclusion}\"";
        var json = $$"""
            { "number": 1, "headRefName": "main", "additions": 0, "deletions": 0, "url": "https://github.com/o/r/pull/1",
              "statusCheckRollup": [
                { "__typename": "CheckRun", "name": "c", "status": "{{status}}", "conclusion": {{conclusionJson}} }
              ]
            }
            """;

        var parsed = SessionPullRequestStatusClient.Parse(json);

        Assert.Equal(expected, parsed!.Checks[0].State.ToString());
    }

    // AC-6's visibility rule for the parsing seam: nothing that isn't a genuine, complete PR object yields a
    // status a caller could render — a caller only ever hides the banner on null, never renders a half-built one.
    [Theory]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("null")]
    public void Parse_ToleratesEmptyOrInvalidJson(string json)
    {
        Assert.Null(SessionPullRequestStatusClient.Parse(json));
    }

    [Fact]
    public async Task GetOpenPullRequestAsync_ANonExistentWorkingDirectory_ReturnsNull()
    {
        var client = new SessionPullRequestStatusClient();

        var status = await client.GetOpenPullRequestAsync(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()), CancellationToken.None);

        Assert.Null(status);
    }

    // OverallState's priority (failure beats running beats passed) is what the collapsed dot/CI-summary text reads
    // off — the mockup's second frame (3/4 passed, one still running) shows amber, not green, and its third frame
    // (one failed among others) shows red even though not every check has finished.
    [Theory]
    [InlineData(new[] { "Passed", "Passed", "Passed", "Passed" }, "Passed")]
    [InlineData(new[] { "Passed", "Passed", "Passed", "Running" }, "Running")]
    [InlineData(new[] { "Passed", "Failed", "Running" }, "Failed")]
    [InlineData(new[] { "Other" }, "Other")]
    public void OverallState_PrioritizesFailureThenRunningThenPassed(string[] checkStates, string expected)
    {
        var checks = checkStates
            .Select((state, i) => new PullRequestCheck($"c{i}", Enum.Parse<PullRequestCheckState>(state), null))
            .ToList();

        var status = new SessionPullRequestStatus(1, "o/r", "main", 0, 0, "https://github.com/o/r/pull/1", checks);

        Assert.Equal(expected, status.OverallState.ToString());
    }

    private static (string Name, PullRequestCheckState State, TimeSpan? Duration) _Tuple(PullRequestCheck check) =>
        (check.Name, check.State, check.Duration);
}
