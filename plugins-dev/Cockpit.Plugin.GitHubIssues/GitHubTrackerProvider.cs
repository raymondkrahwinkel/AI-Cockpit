using Cockpit.Plugins.Abstractions.Tracking;

namespace Cockpit.Plugin.GitHubIssues;

/// <summary>
/// The GitHub-Issues half of the tracker-provider contract (AC-154): posts a comment and adds a label (a GitHub issue
/// has no status field, so a label is its stage-equivalent) through the <c>gh</c> CLI. GitHub Issues have no attachment
/// channel, so <see cref="AttachAsync"/> reports it did not land and a consumer falls back to a comment with a link.
/// Every action returns whether it landed rather than throwing.
/// </summary>
internal sealed class GitHubTrackerProvider : ITrackerProvider
{
    private readonly GitHubWorkflowClient _client = new();

    public string TrackerId => "github-issues";

    public async Task<bool> PostCommentAsync(string issueId, string comment, CancellationToken cancellationToken = default)
    {
        if (_Reference(issueId) is not { } issue)
        {
            return false;
        }

        try
        {
            await _client.CommentAsync(issue, comment, cancellationToken);
            return true;
        }
        catch (Exception)
        {
            // A tracker action that fails degrades to "did not land" — the contract's whole point — never a crash of the run.
            return false;
        }
    }

    public async Task<bool> SetStageAsync(string issueId, string stage, CancellationToken cancellationToken = default)
    {
        if (_Reference(issueId) is not { } issue)
        {
            return false;
        }

        try
        {
            await _client.AddLabelAsync(issue, stage, cancellationToken);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public Task<bool> AttachAsync(string issueId, string fileName, byte[] content, string mediaType, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    private static GitHubIssueReference? _Reference(string issueId)
    {
        try
        {
            return GitHubIssueReference.Parse(issueId, string.Empty);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
