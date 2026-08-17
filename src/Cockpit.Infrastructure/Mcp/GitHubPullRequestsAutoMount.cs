using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Core.Mcp;

namespace Cockpit.Infrastructure.Mcp;

// AC-869: mounts the Internal cockpit-github-pull-requests endpoint for a session whose working directory is a
// git repository, with no operator config. Shared by every route that resolves a session's MCP selection. The
// assistant's own always-on rule lives separately, in AssistantSessionHost.McpSelection.
internal static class GitHubPullRequestsAutoMount
{
    public static async Task<IReadOnlySet<string>> NamesAsync(IWorktreeManager? worktreeManager, string? workingDirectory, CancellationToken cancellationToken)
    {
        if (worktreeManager is null || string.IsNullOrWhiteSpace(workingDirectory))
        {
            return NoNames;
        }

        var repository = await worktreeManager.DetectRepositoryAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        return repository is null ? NoNames : new HashSet<string>(StringComparer.OrdinalIgnoreCase) { GitHubPullRequestsMcp.ServerName };
    }

    private static readonly IReadOnlySet<string> NoNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}
