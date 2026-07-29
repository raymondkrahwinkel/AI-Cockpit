using System.Diagnostics;
using Cockpit.App.Services;
using Cockpit.Core.Abstractions.Workspaces;
using Cockpit.Core.Workspaces;
using Cockpit.Infrastructure.Worktrees;
using Cockpit.TestSupport;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-410 design decision 4: <c>Program.cs</c> must reconcile the worktree registry against
/// <see cref="SessionRestoreRoster.PaneIdsAsync"/>'s set, not an empty one — a worktree belonging to a pane a
/// restore may still bring back must not read as an orphan and be swept, which was the bug before panes were
/// persisted. Exercises the same call <c>Program.ReconcileWorktreesAndCompactStateAsync</c> makes
/// (<c>IWorktreeManager.ReconcileAsync</c> against the roster) against a real git repository, the way
/// <c>WorktreeManagerTests</c> does — a fake git would not prove what git itself does with an orphaned worktree.
/// </summary>
public sealed class SessionRestoreWorktreeReconcileTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"cockpit-restore-reconcile-{Guid.NewGuid():n}");
    private readonly string _repo;
    private readonly string _worktreesRoot;
    private readonly WorktreeManager _manager;

    public SessionRestoreWorktreeReconcileTests()
    {
        _repo = Path.Combine(_tempRoot, "repo");
        _worktreesRoot = Path.Combine(_tempRoot, "worktrees");
        var configPath = Path.Combine(_tempRoot, "cockpit.json");

        Directory.CreateDirectory(_repo);
        _Git(_repo, "init", "-b", "main");
        _Git(_repo, "config", "user.email", "test@example.com");
        _Git(_repo, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(_repo, "README.md"), "hello\n");
        _Git(_repo, "add", "-A");
        _Git(_repo, "commit", "-m", "first");

        _manager = new WorktreeManager(new WorktreeRegistryStore(configPath), _worktreesRoot);
    }

    public void Dispose() => TestGitDirectory.Remove(_tempRoot);

    [Fact]
    public async Task ReconcileAgainstTheRestoreRoster_KeepsARestorablePanesWorktree_ButRemovesAnUnknownOnes()
    {
        var restorablePane = new WorkspacePane("restorable-pane", PaneKind.AiSession) { ProfileId = "work" };
        var sessionsWorkspace = Workspace.Create("Work", WorkspaceType.Sessions).WithPane(restorablePane);
        var settings = new WorkspaceSettings { Workspaces = [sessionsWorkspace], ActiveWorkspaceId = sessionsWorkspace.Id };

        var store = Substitute.For<IWorkspaceSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(settings);

        // One worktree owned by the pane cockpit.json still names (a restore may yet reattach it), one owned by a
        // pane id that appears nowhere — the unknown-crash-orphan case the reconcile has always swept.
        var restorableWorktree = await _manager.CreateAsync("restorable-pane", "cockpit/restorable", _repo);
        var unknownWorktree = await _manager.CreateAsync("unknown-pane", "cockpit/unknown", _repo);

        var roster = await SessionRestoreRoster.PaneIdsAsync(store);
        await _manager.ReconcileAsync(roster);

        Assert.True(Directory.Exists(restorableWorktree.Path), "a restorable pane's worktree must survive the reconcile");
        Assert.False(Directory.Exists(unknownWorktree.Path), "a worktree for a pane the roster does not know must still be swept");

        var remaining = await _manager.ListAsync();
        var remainingRecord = Assert.Single(remaining);
        Assert.Equal("restorable-pane", remainingRecord.SessionId);
    }

    private static string _Git(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("git did not start.");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {standardError.Trim()}");
        }

        return standardOutput.Trim();
    }
}
