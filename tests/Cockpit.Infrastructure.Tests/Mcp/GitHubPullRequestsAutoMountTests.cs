using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Core.Mcp;
using Cockpit.Core.Worktrees;
using Cockpit.Infrastructure.Mcp;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Mcp;

/// <summary>
/// AC-869: cockpit-github-pull-requests is Internal (hidden from every picker) and mounts itself only for a
/// session whose working directory is a git repository — no operator config, no checklist row.
/// </summary>
public class GitHubPullRequestsAutoMountTests
{
    private static readonly GitRepositoryInfo Repository = new("/repo", "abc123", "main");

    [Fact]
    public async Task NamesAsync_AGitRepositoryWorkingDirectory_NamesTheServer()
    {
        var worktrees = Substitute.For<IWorktreeManager>();
        worktrees.DetectRepositoryAsync("/repo", Arg.Any<CancellationToken>()).Returns(Repository);

        var names = await GitHubPullRequestsAutoMount.NamesAsync(worktrees, "/repo", CancellationToken.None);

        Assert.Contains(GitHubPullRequestsMcp.ServerName, names);
    }

    [Fact]
    public async Task NamesAsync_ANonGitWorkingDirectory_DoesNotNameTheServer()
    {
        var worktrees = Substitute.For<IWorktreeManager>();
        worktrees.DetectRepositoryAsync("/not-a-repo", Arg.Any<CancellationToken>()).Returns((GitRepositoryInfo?)null);

        var names = await GitHubPullRequestsAutoMount.NamesAsync(worktrees, "/not-a-repo", CancellationToken.None);

        Assert.Empty(names);
    }

    [Fact]
    public async Task NamesAsync_NoWorkingDirectory_DoesNotNameTheServer()
    {
        var worktrees = Substitute.For<IWorktreeManager>();

        var names = await GitHubPullRequestsAutoMount.NamesAsync(worktrees, workingDirectory: null, CancellationToken.None);

        Assert.Empty(names);
        await worktrees.DidNotReceive().DetectRepositoryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NamesAsync_NoWorktreeManager_DoesNotNameTheServer()
    {
        // A caller with no worktree manager wired (a unit test, a driver that never got one) must not throw — the
        // rule simply never fires, same as any other best-effort MCP resolution in this codebase.
        var names = await GitHubPullRequestsAutoMount.NamesAsync(worktreeManager: null, "/repo", CancellationToken.None);

        Assert.Empty(names);
    }
}
