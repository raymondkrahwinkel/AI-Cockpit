namespace Cockpit.Plugin.GitHubPullRequests.Tests;

// AC-675: the "Updated" column bound `UpdatedAt` directly, which the GitHub API always returns in UTC — the
// grid rendered the UTC clock as if it were the operator's own. `UpdatedAtLocal` is what the grid binds to now.
public class GitHubPullRequestTests
{
    [Fact]
    public void UpdatedAtLocal_ConvertsFromUtcToTheOperatorsLocalOffset()
    {
        var updatedAtUtc = new DateTimeOffset(2026, 6, 15, 9, 30, 0, TimeSpan.Zero);
        var pullRequest = new GitHubPullRequest(1, "Title", "https://example/pr/1", null, "owner/repo", "author", updatedAtUtc);

        Assert.Equal(updatedAtUtc.ToLocalTime(), pullRequest.UpdatedAtLocal);
        Assert.Equal(TimeZoneInfo.Local.GetUtcOffset(updatedAtUtc), pullRequest.UpdatedAtLocal!.Value.Offset);
    }

    [Fact]
    public void UpdatedAtLocal_IsNullWhenUpdatedAtIsNull()
    {
        var pullRequest = new GitHubPullRequest(1, "Title", "https://example/pr/1", null, "owner/repo", "author");

        Assert.Null(pullRequest.UpdatedAtLocal);
    }
}
