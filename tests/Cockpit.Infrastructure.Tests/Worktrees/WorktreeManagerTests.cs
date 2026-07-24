using System.Diagnostics;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Core.Worktrees;
using Cockpit.Infrastructure.Worktrees;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Worktrees;

/// <summary>
/// The worktree manager against a real git repository (AC-85). A fake git would prove nothing: what this promises
/// is about what git actually does with an existing branch, a dirty tree, a detached head — and about the
/// isolation two worktrees on one repository give each other.
/// </summary>
public sealed class WorktreeManagerTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"cockpit-worktree-{Guid.NewGuid():n}");
    private readonly string _repo;
    private readonly string _worktreesRoot;
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private readonly WorktreeManager _manager;

    public WorktreeManagerTests()
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

    [Fact]
    public async Task DetectRepositoryAsync_DirectoryOutsideAnyRepository_ReturnsNull()
    {
        var plain = Path.Combine(_tempRoot, "not-a-repo");
        Directory.CreateDirectory(plain);

        var info = await _manager.DetectRepositoryAsync(plain);

        info.Should().BeNull();
    }

    [Fact]
    public async Task DetectRepositoryAsync_Repository_ReportsRootBranchAndHead()
    {
        var info = await _manager.DetectRepositoryAsync(_repo);

        info.Should().NotBeNull();
        info!.Root.Should().Be(Path.GetFullPath(_Git(_repo, "rev-parse", "--show-toplevel")));
        info.CurrentBranch.Should().Be("main");
        info.IsDetachedHead.Should().BeFalse();
        info.HeadCommit.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task DetectRepositoryAsync_DetachedHead_ReportsDetachedWithNoBranch()
    {
        _Git(_repo, "checkout", _Git(_repo, "rev-parse", "HEAD"));

        var info = await _manager.DetectRepositoryAsync(_repo);

        info.Should().NotBeNull();
        info!.IsDetachedHead.Should().BeTrue();
        info.CurrentBranch.Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_NewBranch_CreatesWorktreeOutsideRepoAndRecordsIt()
    {
        const string branch = "cockpit/ac-85-work";

        var record = await _manager.CreateAsync(_sessionId, branch, _repo);

        Directory.Exists(record.Path).Should().BeTrue();
        record.Path.Should().StartWith(Path.GetFullPath(_worktreesRoot));
        record.Branch.Should().Be(branch);
        record.BaseCommit.Should().Be(_Git(_repo, "rev-parse", "HEAD"));

        (await _manager.ListAsync()).Should().ContainSingle().Which.Branch.Should().Be(branch);
        _Git(_repo, "branch", "--list", branch).Should().NotBeEmpty();
    }

    [Fact]
    public async Task CreateAsync_RecordsTheBaseBranchItForkedFrom()
    {
        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);

        // The branch it forked from is what the cleanup check later measures "unmerged" against, so it must survive a
        // round-trip through the registry rather than only living on the in-memory record.
        record.BaseBranch.Should().Be("main");
        (await _manager.ListAsync()).Should().ContainSingle().Which.BaseBranch.Should().Be("main");
    }

    [Fact]
    public async Task CreateAsync_BranchThatAlreadyExists_FailsLoudly_WithoutResettingIt()
    {
        const string branch = "already-here";
        _Git(_repo, "branch", branch);

        var create = async () => await _manager.CreateAsync(_sessionId, branch, _repo);

        (await create.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*already exists*");
    }

    [Fact]
    public async Task CreateAsync_DirectoryNotARepository_Throws()
    {
        var plain = Path.Combine(_tempRoot, "plain");
        Directory.CreateDirectory(plain);

        var create = async () => await _manager.CreateAsync(_sessionId, "branch", plain);

        await create.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task CreateAsync_WorktreeRootInsideRepository_IsRefused()
    {
        // The state root is always outside the repo in production; this drives a manager whose root is inside it, to
        // prove the guard that a worktree is never checked out into the tree it is meant to keep clean.
        var manager = new WorktreeManager(new WorktreeRegistryStore(Path.Combine(_tempRoot, "inside.json")), Path.Combine(_repo, "nested"));

        var create = async () => await manager.CreateAsync(_sessionId, "branch", _repo);

        (await create.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*inside the repository*");
    }

    [Fact]
    public async Task IsCleanAsync_FreshWorktree_IsClean()
    {
        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);

        (await _manager.IsCleanAsync(record)).Should().BeTrue();
    }

    [Fact]
    public async Task IsCleanAsync_WorktreeWithUncommittedChange_IsNotClean()
    {
        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);
        File.WriteAllText(Path.Combine(record.Path, "change.txt"), "work\n");

        (await _manager.IsCleanAsync(record)).Should().BeFalse();
    }

    [Fact]
    public async Task IsCleanAsync_WorktreeWithCommitAheadOfBase_IsNotClean()
    {
        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);
        File.WriteAllText(Path.Combine(record.Path, "change.txt"), "work\n");
        _Git(record.Path, "add", "-A");
        _Git(record.Path, "commit", "-m", "work");

        (await _manager.IsCleanAsync(record)).Should().BeFalse();
    }

    [Fact]
    public async Task RemoveAsync_CleanWorktree_RemovesFolderAndRegistryEntry_ButKeepsTheBranch()
    {
        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);

        await _manager.RemoveAsync(record);

        Directory.Exists(record.Path).Should().BeFalse();
        (await _manager.ListAsync()).Should().BeEmpty();
        // Removing a worktree does not remove its branch — branch cleanup is the teardown policy's decision (F3),
        // not this primitive's, so a forced removal can still keep the commits on the branch.
        _Git(_repo, "branch", "--list", "wt").Should().NotBeEmpty();
    }

    [Fact]
    public async Task RemoveAsync_DirtyWorktreeWithoutForce_IsRefused_ThenForceRemoves()
    {
        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);
        File.WriteAllText(Path.Combine(record.Path, "change.txt"), "work\n");

        var remove = async () => await _manager.RemoveAsync(record);
        await remove.Should().ThrowAsync<InvalidOperationException>();
        Directory.Exists(record.Path).Should().BeTrue();

        await _manager.RemoveAsync(record, force: true);
        Directory.Exists(record.Path).Should().BeFalse();
        (await _manager.ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_TwoSessionsOnOneRepository_GiveEachOtherIsolatedTrees()
    {
        var first = await _manager.CreateAsync(Guid.NewGuid().ToString("n"), "cockpit/session-1", _repo);
        var second = await _manager.CreateAsync(Guid.NewGuid().ToString("n"), "cockpit/session-2", _repo);

        first.Path.Should().NotBe(second.Path);
        first.Branch.Should().NotBe(second.Branch);
        (await _manager.ListAsync()).Should().HaveCount(2);

        File.WriteAllText(Path.Combine(first.Path, "only-in-first.txt"), "x\n");
        _Git(first.Path, "add", "-A");
        _Git(first.Path, "commit", "-m", "first-only work");

        File.Exists(Path.Combine(second.Path, "only-in-first.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task ReleaseAsync_CleanWorktree_RemovesItAndDeletesItsBranch()
    {
        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);

        await _manager.ReleaseAsync(_sessionId);

        Directory.Exists(record.Path).Should().BeFalse();
        (await _manager.ListAsync()).Should().BeEmpty();
        // Unlike a bare RemoveAsync, teardown of a clean worktree also deletes its (work-free) branch.
        _Git(_repo, "branch", "--list", "wt").Should().BeEmpty();
    }

    [Fact]
    public async Task ReleaseAsync_WorktreeWithUncommittedWork_KeepsItAndMarksItRetained()
    {
        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);
        File.WriteAllText(Path.Combine(record.Path, "work.txt"), "unfinished\n");

        await _manager.ReleaseAsync(_sessionId);

        Directory.Exists(record.Path).Should().BeTrue();
        var retained = (await _manager.ListAsync()).Should().ContainSingle().Subject;
        retained.IsRetained.Should().BeTrue();
        retained.Path.Should().Be(record.Path);
        _Git(_repo, "branch", "--list", "wt").Should().NotBeEmpty();
    }

    [Fact]
    public async Task ReconcileAsync_RemovesAnOrphanedCleanWorktree_ButKeepsALiveOne()
    {
        var orphan = await _manager.CreateAsync(Guid.NewGuid().ToString("n"), "cockpit/orphan", _repo);
        var live = await _manager.CreateAsync(Guid.NewGuid().ToString("n"), "cockpit/live", _repo);

        await _manager.ReconcileAsync([live.SessionId]);

        Directory.Exists(orphan.Path).Should().BeFalse();
        Directory.Exists(live.Path).Should().BeTrue();
        (await _manager.ListAsync()).Should().ContainSingle().Which.SessionId.Should().Be(live.SessionId);
    }

    [Fact]
    public async Task GetStatusesAsync_ReportsClean_ThenDirty_ThenHoldingACommitThatExistsNowhereElse()
    {
        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);

        (await _manager.GetStatusesAsync()).Should().ContainSingle().Which.IsClean.Should().BeTrue();

        File.WriteAllText(Path.Combine(record.Path, "change.txt"), "work\n");
        var dirty = (await _manager.GetStatusesAsync()).Single();
        dirty.HasUncommittedChanges.Should().BeTrue();
        dirty.IsClean.Should().BeFalse();

        _Git(record.Path, "add", "-A");
        _Git(record.Path, "commit", "-m", "work");
        var holdingWork = (await _manager.GetStatusesAsync()).Single();
        holdingWork.StrandableCommits.Should().Be(1);
        holdingWork.IsClean.Should().BeFalse();
    }

    [Fact]
    public async Task GetStatusesAsync_WorktreeWhoseCommitsAreMergedIntoBase_ReadsAsClean()
    {
        // A finished session's worktree: it made a commit that has since been merged into main. Its work is safe on
        // main, so removing the worktree loses nothing — the panel and the clean-gate must read it as clean and let
        // "clean up finished" sweep it, not show "commit ahead" forever because the fork point never moves (AC-85).
        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);
        File.WriteAllText(Path.Combine(record.Path, "change.txt"), "work\n");
        _Git(record.Path, "add", "-A");
        _Git(record.Path, "commit", "-m", "work");

        _Git(_repo, "merge", "--no-ff", "wt", "-m", "merge wt");

        var status = (await _manager.GetStatusesAsync()).Single();
        status.StrandableCommits.Should().Be(0);
        status.IsClean.Should().BeTrue();
        (await _manager.IsCleanAsync(record)).Should().BeTrue();
    }

    [Fact]
    public async Task IsCleanAsync_WorktreeWhoseCommitIsPushedButNotMerged_IsClean()
    {
        // Raymond's rule (AC-266): pushed is safe. The session is gone and its commit lives on the remote, so
        // removing the folder loses nothing — waiting for a merge would keep every finished worktree until its PR
        // lands, which is exactly the pile-up the isolated-workspace switch was supposed to avoid.
        _AddRemote();
        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);
        _Commit(record.Path, "change.txt", "work\n");
        _Git(record.Path, "push", "origin", "wt");

        (await _manager.IsCleanAsync(record)).Should().BeTrue();
    }

    [Fact]
    public async Task IsCleanAsync_WorktreeWithACommitThatWasNeverPushed_IsNotClean()
    {
        // The guard on the rule above: a remote existing must not make everything read as safe. Only work that is
        // actually on it counts — this is the side where being wrong loses commits.
        _AddRemote();
        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);
        _Commit(record.Path, "change.txt", "work\n");

        (await _manager.IsCleanAsync(record)).Should().BeFalse();
    }

    [Fact]
    public async Task IsCleanAsync_WorktreeSquashMergedIntoBase_IsClean()
    {
        // The squash-merge GitHub does on a PR: the base holds the work under a brand-new commit, so the branch's own
        // commit is reachable from nowhere and counting history by identity calls it unmerged forever (AC-266).
        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);
        _Commit(record.Path, "change.txt", "work\n");

        _Git(_repo, "merge", "--squash", "wt");
        _Git(_repo, "commit", "-m", "squashed wt");

        (await _manager.IsCleanAsync(record)).Should().BeTrue();
    }

    [Fact]
    public async Task IsCleanAsync_WorktreeWithSeveralCommitsSquashedIntoBase_IsClean()
    {
        // The same merge on a branch that took more than one commit — the case patch-id comparison cannot see, since
        // the single squashed commit matches none of the originals. The files it touched decide instead.
        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);
        _Commit(record.Path, "one.txt", "first\n");
        _Commit(record.Path, "two.txt", "second\n");
        _Commit(record.Path, "one.txt", "first, revised\n");

        _Git(_repo, "merge", "--squash", "wt");
        _Git(_repo, "commit", "-m", "squashed wt");

        (await _manager.IsCleanAsync(record)).Should().BeTrue();
    }

    [Fact]
    public async Task IsCleanAsync_MergedOnTheRemoteWhileTheLocalBaseLagsBehind_IsClean()
    {
        // The second half of what Raymond hit: the merge landed on origin/main, but his local main had not been
        // pulled since. Measuring against the local tip alone reports merged work as unmerged, so the base ref must
        // follow whichever tip this repository knows to be further along.
        _AddRemote();
        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);
        _Commit(record.Path, "change.txt", "work\n");

        _Git(_repo, "merge", "--squash", "wt");
        _Git(_repo, "commit", "-m", "squashed wt");
        _Git(_repo, "push", "origin", "main");
        _Git(_repo, "reset", "--hard", "HEAD~1");

        (await _manager.IsCleanAsync(record)).Should().BeTrue();
    }

    [Fact]
    public async Task IsCleanAsync_MergedWorktreeWithoutARecordedBaseBranch_IsClean_ViaDefaultBranchFallback()
    {
        // The crash net for worktrees registered before the base branch was tracked (BaseBranch is null): the check
        // falls back to the repository's default branch, so an already-merged orphan still reads clean and cleans up
        // rather than lingering forever — the state Raymond hit with three merged, session-gone trees.
        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);
        File.WriteAllText(Path.Combine(record.Path, "change.txt"), "work\n");
        _Git(record.Path, "add", "-A");
        _Git(record.Path, "commit", "-m", "work");
        _Git(_repo, "merge", "--no-ff", "wt", "-m", "merge wt");

        var legacy = record with { BaseBranch = null };

        (await _manager.IsCleanAsync(legacy)).Should().BeTrue();
    }

    [Fact]
    public async Task IsCleanAsync_WorktreeWhoseOnlyUnmergedCommitIsAMerge_IsNotClean()
    {
        // An evil merge: every ordinary commit it carries is already in the base, and the only thing that is not lives
        // in the merge commit's own tree. `git cherry` prints no line at all for a merge, so the patch comparison sees
        // an empty answer and would call the branch fully present. That merge's content exists nowhere else.
        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);

        _Git(_repo, "checkout", "-b", "side");
        _Commit(_repo, "side.txt", "side work\n");
        _Git(_repo, "checkout", "main");
        _Git(_repo, "merge", "--no-ff", "side", "-m", "merge side into main");

        _Git(record.Path, "merge", "--no-ff", "--no-commit", "side");
        File.WriteAllText(Path.Combine(record.Path, "resolved.txt"), "only in the merge\n");
        _Git(record.Path, "add", "-A");
        _Git(record.Path, "commit", "-m", "merge side, with a fix of its own");

        (await _manager.IsCleanAsync(record)).Should().BeFalse();
    }

    [Fact]
    public async Task IsCleanAsync_UnmergedWorkInAFileGitQuotes_IsNotClean()
    {
        // git renders a non-ASCII path as "caf\303\251.txt" — quoted and octal-escaped — and a pathspec built from
        // that text matches no file, which git reports as "no difference": the content check's safe-looking answer
        // for a branch whose work it never actually compared.
        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);
        _Commit(record.Path, "café.txt", "unmerged work\n");

        (await _manager.IsCleanAsync(record)).Should().BeFalse();
    }

    [Fact]
    public async Task ReleaseAsync_WorktreeSafeOnlyBecauseItWasPushed_KeepsTheBranch()
    {
        // Removing the folder is fine — a checkout is reproducible — but the proof it is safe is a remote-tracking
        // ref, and that is this repository's last view of a remote, not the remote. A force-push or a deleted remote
        // branch makes it a lie, and the branch is then the only place those commits still live.
        // Pushed with -u, the way a session that opened a PR leaves it: git itself would then allow `branch -d`,
        // because the branch is merged into its upstream. That permission is exactly what must not be taken.
        _AddRemote();
        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);
        _Commit(record.Path, "change.txt", "work\n");
        _Git(record.Path, "push", "-u", "origin", "wt");

        await _manager.ReleaseAsync(_sessionId);

        Directory.Exists(record.Path).Should().BeFalse();
        _Git(_repo, "branch", "--list", "wt").Should().Contain("wt");
    }

    [Fact]
    public async Task ReleaseAsync_WorktreeWhoseWorkIsInTheBase_DropsTheBranchToo()
    {
        // The other side of the rule above: once the work is in the base itself there is nothing the branch still
        // holds, and leaving it would pile up a dead ref per finished session.
        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);
        _Commit(record.Path, "change.txt", "work\n");
        _Git(_repo, "merge", "--no-ff", "wt", "-m", "merge wt");

        await _manager.ReleaseAsync(_sessionId);

        Directory.Exists(record.Path).Should().BeFalse();
        _Git(_repo, "branch", "--list", "wt").Should().BeEmpty();
    }

    [Fact]
    public async Task IsCleanAsync_MergedOnASlashNamedBaseBranchTrackedOnARemote_IsClean()
    {
        // The base branch a session forks from is not always 'main': a session started on 'feat/thing' records that
        // as its base, and the local-lags-behind fix has to work for it just the same.
        _AddRemote();
        _Git(_repo, "checkout", "-b", "feat/thing");
        _Commit(_repo, "feature.txt", "feature\n");
        _Git(_repo, "push", "-u", "origin", "feat/thing");

        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);
        _Commit(record.Path, "change.txt", "work\n");

        _Git(_repo, "merge", "--squash", "wt");
        _Git(_repo, "commit", "-m", "squashed wt");
        _Git(_repo, "push", "origin", "feat/thing");
        _Git(_repo, "reset", "--hard", "HEAD~1");

        (await _manager.IsCleanAsync(record)).Should().BeTrue();
    }

    [Fact]
    public async Task ReattachAsync_ReassignsTheWorktreeToANewSession()
    {
        var record = await _manager.CreateAsync(Guid.NewGuid().ToString("n"), "cockpit/orphan", _repo);
        var newSession = Guid.NewGuid().ToString("n");

        var reattached = await _manager.ReattachAsync(record.Path, newSession);

        reattached.Should().NotBeNull();
        reattached!.SessionId.Should().Be(newSession);
        reattached.IsRetained.Should().BeFalse();
        (await _manager.ListAsync()).Should().ContainSingle().Which.SessionId.Should().Be(newSession);
    }

    [Fact]
    public async Task ReattachAsync_UnknownPath_ReturnsNull()
    {
        var reattached = await _manager.ReattachAsync(Path.Combine(_tempRoot, "nope"), Guid.NewGuid().ToString("n"));

        reattached.Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_PlacesTheWorktreeUnderTheConfiguredRootOverride()
    {
        var customRoot = Path.Combine(_tempRoot, "custom-worktree-root");
        var settings = Substitute.For<IWorktreeSettingsStore>();
        settings.LoadAsync(Arg.Any<CancellationToken>()).Returns(new WorktreeSettings { Root = customRoot });
        var manager = new WorktreeManager(new WorktreeRegistryStore(Path.Combine(_tempRoot, "override.json")), settings);

        var record = await manager.CreateAsync(_sessionId, "wt", _repo);

        record.Path.Should().StartWith(Path.GetFullPath(customRoot));
        Directory.Exists(record.Path).Should().BeTrue();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    /// <summary>A bare repository as origin, with main already on it — the "has somewhere to be pushed to" fixture.</summary>
    private void _AddRemote()
    {
        var remote = Path.Combine(_tempRoot, "remote.git");
        _Git(_tempRoot, "init", "--bare", remote);
        _Git(_repo, "remote", "add", "origin", remote);
        _Git(_repo, "push", "-u", "origin", "main");
    }

    private static void _Commit(string workingDirectory, string file, string content)
    {
        File.WriteAllText(Path.Combine(workingDirectory, file), content);
        _Git(workingDirectory, "add", "-A");
        _Git(workingDirectory, "commit", "-m", $"work on {file}");
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
