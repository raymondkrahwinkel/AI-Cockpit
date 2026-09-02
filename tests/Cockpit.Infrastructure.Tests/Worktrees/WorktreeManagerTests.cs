using System.Runtime.Versioning;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Core.Worktrees;
using Cockpit.Infrastructure.Worktrees;
using Cockpit.TestSupport;
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
    private readonly string _configPath;
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private readonly WorktreeRegistryStore _registry;
    private readonly WorktreeManager _manager;

    public WorktreeManagerTests()
    {
        _repo = Path.Combine(_tempRoot, "repo");
        _worktreesRoot = Path.Combine(_tempRoot, "worktrees");
        _configPath = Path.Combine(_tempRoot, "cockpit.json");

        Directory.CreateDirectory(_repo);
        _Git(_repo, "init", "-b", "main");
        _Git(_repo, "config", "user.email", "test@example.com");
        _Git(_repo, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(_repo, "README.md"), "hello\n");
        _Git(_repo, "add", "-A");
        _Git(_repo, "commit", "-m", "first");

        _registry = new WorktreeRegistryStore(_configPath);
        _manager = new WorktreeManager(_registry, _worktreesRoot);
    }

    [Fact]
    public async Task DetectRepositoryAsync_DirectoryOutsideAnyRepository_ReturnsNull()
    {
        var plain = Path.Combine(_tempRoot, "not-a-repo");
        Directory.CreateDirectory(plain);

        var info = await _manager.DetectRepositoryAsync(plain);

        Assert.Null(info);
    }

    [Fact]
    public async Task DetectRepositoryAsync_Repository_ReportsRootBranchAndHead()
    {
        var info = await _manager.DetectRepositoryAsync(_repo);

        Assert.NotNull(info);
        Assert.Equal(Path.GetFullPath(_Git(_repo, "rev-parse", "--show-toplevel")), info!.Root);
        Assert.Equal("main", info.CurrentBranch);
        Assert.False(info.IsDetachedHead);
        Assert.False(string.IsNullOrEmpty(info.HeadCommit));
    }

    [Fact]
    public async Task DetectRepositoryAsync_DetachedHead_ReportsDetachedWithNoBranch()
    {
        _Git(_repo, "checkout", _Git(_repo, "rev-parse", "HEAD"));

        var info = await _manager.DetectRepositoryAsync(_repo);

        Assert.NotNull(info);
        Assert.True(info!.IsDetachedHead);
        Assert.Null(info.CurrentBranch);
    }

    [Fact]
    public async Task CreateAsync_NewBranch_CreatesWorktreeOutsideRepoAndRecordsIt()
    {
        const string branch = "cockpit/ac-85-work";

        var record = await _manager.CreateAsync(_sessionId, branch, _repo);

        Assert.True(Directory.Exists(record.Path));
        Assert.StartsWith(Path.GetFullPath(_worktreesRoot), record.Path);
        Assert.Equal(branch, record.Branch);
        Assert.Equal(_Git(_repo, "rev-parse", "HEAD"), record.BaseCommit);

        Assert.Equal(branch, Assert.Single((await _manager.ListAsync())).Branch);
        Assert.NotEmpty(_Git(_repo, "branch", "--list", branch));
    }

    [Fact]
    public async Task CreateAsync_RecordsTheBaseBranchItForkedFrom()
    {
        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);

        // The branch it forked from is what the cleanup check later measures "unmerged" against, so it must survive a
        // round-trip through the registry rather than only living on the in-memory record.
        Assert.Equal("main", record.BaseBranch);
        Assert.Equal("main", Assert.Single((await _manager.ListAsync())).BaseBranch);
    }

    [Fact]
    public async Task CreateAsync_BranchThatAlreadyExists_FailsLoudly_WithoutResettingIt()
    {
        const string branch = "already-here";
        _Git(_repo, "branch", branch);

        var create = async () => await _manager.CreateAsync(_sessionId, branch, _repo);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(create);
        Assert.Contains("already exists", ex.Message);
    }

    [Fact]
    public async Task CreateAsync_DirectoryNotARepository_Throws()
    {
        var plain = Path.Combine(_tempRoot, "plain");
        Directory.CreateDirectory(plain);

        var create = async () => await _manager.CreateAsync(_sessionId, "branch", plain);

        await Assert.ThrowsAsync<InvalidOperationException>(create);
    }

    [Fact]
    public async Task CreateAsync_WorktreeRootInsideRepository_IsRefused()
    {
        // The state root is always outside the repo in production; this drives a manager whose root is inside it, to
        // prove the guard that a worktree is never checked out into the tree it is meant to keep clean.
        var manager = new WorktreeManager(new WorktreeRegistryStore(Path.Combine(_tempRoot, "inside.json")), Path.Combine(_repo, "nested"));

        var create = async () => await manager.CreateAsync(_sessionId, "branch", _repo);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(create);
        Assert.Contains("inside the repository", ex.Message);
    }

    [Fact]
    public async Task IsCleanAsync_FreshWorktree_IsClean()
    {
        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);

        Assert.True((await _manager.IsCleanAsync(record)));
    }

    [Fact]
    public async Task IsCleanAsync_WorktreeWithUncommittedChange_IsNotClean()
    {
        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);
        File.WriteAllText(Path.Combine(record.Path, "change.txt"), "work\n");

        Assert.False((await _manager.IsCleanAsync(record)));
    }

    [Fact]
    public async Task IsCleanAsync_WorktreeWithCommitAheadOfBase_IsNotClean()
    {
        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);
        File.WriteAllText(Path.Combine(record.Path, "change.txt"), "work\n");
        _Git(record.Path, "add", "-A");
        _Git(record.Path, "commit", "-m", "work");

        Assert.False((await _manager.IsCleanAsync(record)));
    }

    [Fact]
    public async Task RemoveAsync_CleanWorktree_RemovesFolderAndRegistryEntry_ButKeepsTheBranch()
    {
        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);

        await _manager.RemoveAsync(record);

        Assert.False(Directory.Exists(record.Path));
        Assert.Empty((await _manager.ListAsync()));
        // Removing a worktree does not remove its branch — branch cleanup is the teardown policy's decision (F3),
        // not this primitive's, so a forced removal can still keep the commits on the branch.
        Assert.NotEmpty(_Git(_repo, "branch", "--list", "wt"));
    }

    [Fact]
    public async Task RemoveAsync_DirtyWorktreeWithoutForce_IsRefused_ThenForceRemoves()
    {
        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);
        File.WriteAllText(Path.Combine(record.Path, "change.txt"), "work\n");

        var remove = async () => await _manager.RemoveAsync(record);
        await Assert.ThrowsAsync<InvalidOperationException>(remove);
        Assert.True(Directory.Exists(record.Path));
        // The refusal keeps the registry entry too: the worktree is still there, so forgetting it would hide a tree
        // holding work from the panel that is meant to show it.
        Assert.Single((await _manager.ListAsync()));

        await _manager.RemoveAsync(record, force: true);
        Assert.False(Directory.Exists(record.Path));
        Assert.Empty((await _manager.ListAsync()));
    }

    [Fact]
    public async Task RemoveAsync_WorktreeGitHasForgotten_DropsTheRegistryEntryRatherThanRefusing()
    {
        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);

        // The state that could not be cleaned up from the panel (AC-342): the folder deleted by hand and git's own
        // administration pruned, so nothing is left of the worktree but the registry entry the row is drawn from.
        // git answers a remove of that path with "is not a working tree", which used to abort before the registry.
        TestGitDirectory.Remove(record.Path);
        _Git(_repo, "worktree", "unlock", record.Path);
        _Git(_repo, "worktree", "prune");
        Assert.Single(_Git(_repo, "worktree", "list").Split('\n'));

        await _manager.RemoveAsync(record);

        Assert.Empty((await _manager.ListAsync()));
    }

    [Fact]
    public async Task RemoveAsync_FolderLeftBehindWithItsCheckoutClearedOut_DropsTheRegistryEntryAndTheEmptyFolder()
    {
        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);

        // The state a panel row got stuck in: the checkout is gone from the folder but the folder itself is still
        // there, and a prune has since reclaimed git's administration — so git answers every removal with "is not a
        // working tree" while the folder on disk keeps the old guard from ever dropping the record.
        _ClearCheckout(record.Path);
        _Git(_repo, "worktree", "unlock", record.Path);
        _Git(_repo, "worktree", "prune");
        Assert.Single(_Git(_repo, "worktree", "list").Split('\n'));

        await _manager.RemoveAsync(record);

        Assert.Empty((await _manager.ListAsync()));
        // The shell goes too: it is empty, so nothing is lost with it, and leaving it behind is what put an
        // undeletable folder in the operator's state directory in the first place.
        Assert.False(Directory.Exists(record.Path));
        Assert.NotEmpty(_Git(_repo, "branch", "--list", "wt"));
    }

    [Fact]
    public async Task RemoveAsync_FolderLeftBehindBeforeAPruneRan_DropsTheRegistryEntryToo()
    {
        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);

        // The same shell, caught before a prune: git still lists the worktree, so it refuses with a different
        // sentence ("validation failed … '.git' does not exist"). Same answer — there is no working copy to keep.
        _ClearCheckout(record.Path);

        await _manager.RemoveAsync(record);

        Assert.Empty((await _manager.ListAsync()));
        Assert.False(Directory.Exists(record.Path));
    }

    [Fact]
    public async Task RemoveAsync_FolderLeftBehindStillHoldingFiles_DropsTheEntryButKeepsWhatIsOnDisk()
    {
        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);
        _ClearCheckout(record.Path);
        _Git(_repo, "worktree", "unlock", record.Path);
        _Git(_repo, "worktree", "prune");
        var leftover = Path.Combine(record.Path, "left-behind.txt");
        File.WriteAllText(leftover, "not git's any more\n");

        await _manager.RemoveAsync(record);

        // Dropping the entry is a claim about what the cockpit manages, never a licence to delete: git cannot see
        // these files, but they are still someone's, so only the registry row goes.
        Assert.Empty((await _manager.ListAsync()));
        Assert.True(File.Exists(leftover));
    }

    [Fact]
    public async Task GetStatusesAsync_FolderLeftBehindWithItsCheckoutClearedOut_ReadsAsNothingToKeep()
    {
        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);
        _ClearCheckout(record.Path);
        _Git(_repo, "worktree", "unlock", record.Path);
        _Git(_repo, "worktree", "prune");

        var status = Assert.Single((await _manager.GetStatusesAsync()));

        // It used to read as "uncommitted changes" — a claim about work nobody can point at, and the reason the
        // sweep skipped an empty folder it should have taken.
        Assert.False(status.HasUncommittedChanges);
        Assert.True(status.WorkingCopyMissing);
        Assert.True(status.NothingToKeep);
        Assert.False(status.IsClean, "nothing about a tree that is not there can be measured");
    }

    [Fact]
    public async Task ReleaseAsync_FolderLeftBehindWithItsCheckoutClearedOut_DropsTheRecord()
    {
        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);
        _Commit(record.Path, "work.txt", "unmerged\n");
        _ClearCheckout(record.Path);

        await _manager.ReleaseAsync(_sessionId);

        // Teardown used to call this "not clean" and retain it, which is how the panel filled with rows for folders
        // holding nothing. The branch survives, so the commit that only lives on it is still reachable.
        Assert.Empty((await _manager.ListAsync()));
        Assert.NotEmpty(_Git(_repo, "branch", "--list", "wt"));
    }

    [Fact]
    public async Task ReleaseAsync_WorktreeWhoseFolderIsGone_DropsTheRecordRatherThanRetainingItForever()
    {
        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);
        _Commit(record.Path, "work.txt", "unmerged\n");
        TestGitDirectory.Remove(record.Path);

        await _manager.ReleaseAsync(_sessionId);

        // Nothing can be measured about a tree that is not there, so teardown used to call it "not clean" and keep
        // the record — leaving behind exactly the row the panel could not remove. The branch survives, so the commit
        // that only lives on it is still reachable.
        Assert.Empty((await _manager.ListAsync()));
        Assert.NotEmpty(_Git(_repo, "branch", "--list", "wt"));
    }

    [Fact]
    public async Task RemoveAsync_WorktreeWhoseRepositoryIsGone_DropsTheRegistryEntry()
    {
        var second = Path.Combine(_tempRoot, "second-repo");
        Directory.CreateDirectory(second);
        _Git(second, "init", "-b", "main");
        _Git(second, "config", "user.email", "test@example.com");
        _Git(second, "config", "user.name", "Test");
        _Commit(second, "README.md", "hello\n");
        var record = await _manager.CreateAsync(Guid.NewGuid().ToString("n"), "wt-second", second);

        // git cannot be asked anything about a repository that is no longer there — it will not even start with that
        // working directory. With the worktree gone as well, the registry entry is all that is left to remove.
        TestGitDirectory.Remove(record.Path);
        TestGitDirectory.Remove(second);

        var notice = await _manager.RemoveAsync(record);

        Assert.Empty((await _manager.ListAsync()));
        // Both are gone, so there is nothing left to mention — this is the plain "there was never anything to lose"
        // path, distinct from the case below where the worktree folder survives its repository.
        Assert.Null(notice);
    }

    [Fact]
    public async Task RemoveAsync_RepositoryGoneButItsWorktreeFolderSurvives_DropsTheEntryLeavesTheFolderAndSaysSo()
    {
        // AC-507's actual production shape: the repository a worktree forked from disappeared (it was itself another
        // worktree whose parent went away), but the four orphaned worktree folders themselves were still on disk,
        // still holding their checkouts — not the "both gone" shape RemoveAsync already handled above. Before the
        // fix, git could not even be asked (its working directory does not exist) — the old guard's misdiagnosis
        // ("is it installed and on PATH?") — and the record.Path check that was meant to catch this asks git a
        // question it cannot answer either, once the repository behind the worktree is gone.
        var second = Path.Combine(_tempRoot, "second-repo");
        Directory.CreateDirectory(second);
        _Git(second, "init", "-b", "main");
        _Git(second, "config", "user.email", "test@example.com");
        _Git(second, "config", "user.name", "Test");
        _Commit(second, "README.md", "hello\n");
        var record = await _manager.CreateAsync(Guid.NewGuid().ToString("n"), "wt-second", second);
        File.WriteAllText(Path.Combine(record.Path, "uncommitted.txt"), "work nobody pushed anywhere\n");

        // The repository moves away; the worktree folder — and the uncommitted file in it — is untouched.
        TestGitDirectory.Remove(second);

        var notice = await _manager.RemoveAsync(record);

        Assert.Empty((await _manager.ListAsync()));
        Assert.True(Directory.Exists(record.Path), "the worktree folder is never touched when its repository is gone");
        Assert.True(File.Exists(Path.Combine(record.Path, "uncommitted.txt")), "content left behind must survive, not be silently discarded");
        Assert.NotNull(notice);
        Assert.Contains(record.Branch, notice);
        Assert.Contains(record.Path, notice);
    }

    [Fact]
    public async Task RemoveAsync_RepositoryGoneAndWorktreeFolderEmpty_DropsTheEmptyFolderWithoutANotice()
    {
        // The other half of the same shape: the repository is gone, but there is genuinely nothing left in the
        // worktree folder to tell the operator about — an empty shell must not manufacture a notice out of it.
        var second = Path.Combine(_tempRoot, "second-repo");
        Directory.CreateDirectory(second);
        _Git(second, "init", "-b", "main");
        _Git(second, "config", "user.email", "test@example.com");
        _Git(second, "config", "user.name", "Test");
        _Commit(second, "README.md", "hello\n");
        var record = await _manager.CreateAsync(Guid.NewGuid().ToString("n"), "wt-second", second);
        _ClearCheckout(record.Path);
        TestGitDirectory.Remove(second);

        var notice = await _manager.RemoveAsync(record);

        Assert.Empty((await _manager.ListAsync()));
        Assert.False(Directory.Exists(record.Path), "an empty leftover shell is swept, same as the repository-present path");
        Assert.Null(notice);
    }

    [Fact]
    public async Task RemoveAsync_RepositoryRootIsAFolderButNoLongerAGitRepository_StillReportsTheLeftoverFolder()
    {
        // Code review finding: `Directory.Exists(record.RepositoryRoot)` alone cannot tell "gone" from "still a
        // folder but its .git was corrupted or removed" — a repository whose grandparent worktree vanished while the
        // middle folder itself survives, say. Both shapes leave git unable to answer anything about the worktree and
        // must report the same way: a notice whenever something on disk survives the drop, regardless of which
        // reason the drop happened for.
        var second = Path.Combine(_tempRoot, "second-repo");
        Directory.CreateDirectory(second);
        _Git(second, "init", "-b", "main");
        _Git(second, "config", "user.email", "test@example.com");
        _Git(second, "config", "user.name", "Test");
        _Commit(second, "README.md", "hello\n");
        var record = await _manager.CreateAsync(Guid.NewGuid().ToString("n"), "wt-second", second);
        File.WriteAllText(Path.Combine(record.Path, "uncommitted.txt"), "work nobody pushed anywhere\n");

        // The repository root folder itself survives, but stops being a repository — the worktree's own gitdir
        // link resolves nowhere just the same as if the whole folder had gone. Moved aside rather than deleted:
        // git marks loose objects read-only, so Directory.Delete(recursive: true) throws UnauthorizedAccessException
        // on Windows and failed this test in its own setup, before a single assertion ran. A rename needs no access
        // to the files it carries, and leaves git exactly as unable to answer.
        Directory.Move(Path.Combine(second, ".git"), Path.Combine(second, ".git-moved-aside"));

        var notice = await _manager.RemoveAsync(record);

        Assert.Empty((await _manager.ListAsync()));
        Assert.True(Directory.Exists(record.Path), "the worktree folder is never touched");
        Assert.True(File.Exists(Path.Combine(record.Path, "uncommitted.txt")));
        Assert.NotNull(notice);
    }

    [Fact]
    public async Task RemoveAsync_LeftoverFolderMatchesWhatIsAlreadyCommitted_DeletesTheFolder()
    {
        // The other half of AC-524's fourth defect: a folder git can no longer recognise as a working tree (its own
        // .git link broken, admin pruned) but that holds nothing beyond what its branch already committed — nothing
        // is lost by deleting it, so "removed in Cockpit" must mean "gone from disk" here too.
        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);
        _BreakWorktreeGitLink(record.Path);
        _Git(_repo, "worktree", "unlock", record.Path);
        _Git(_repo, "worktree", "prune");
        Assert.Single(_Git(_repo, "worktree", "list").Split('\n'));

        var notice = await _manager.RemoveAsync(record);

        Assert.Empty((await _manager.ListAsync()));
        Assert.False(Directory.Exists(record.Path), "everything on disk already matched the branch's own tip, so nothing was lost deleting it");
        Assert.NotNull(notice);
        Assert.Contains("deleted", notice);
    }

    [Fact]
    public async Task RemoveAsync_LeftoverFolderHasAFileThatNoLongerMatchesTheCommittedContent_KeepsTheFolder()
    {
        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);
        File.WriteAllText(Path.Combine(record.Path, "README.md"), "edited after the checkout broke\n");
        _BreakWorktreeGitLink(record.Path);
        _Git(_repo, "worktree", "unlock", record.Path);
        _Git(_repo, "worktree", "prune");

        var notice = await _manager.RemoveAsync(record);

        Assert.Empty((await _manager.ListAsync()));
        Assert.True(Directory.Exists(record.Path), "a file that no longer matches what the branch committed must not be discarded on a guess");
        Assert.True(File.Exists(Path.Combine(record.Path, "README.md")));
        Assert.NotNull(notice);
        Assert.DoesNotContain("deleted", notice);
    }

    [Fact]
    public async Task RemoveAsync_LeftoverFolderHasACommitOnlyItsBranchHas_KeepsTheFolderEvenThoughDiskMatchesTheTip()
    {
        // Disk matching the branch's tip is not enough on its own: the tip itself must be reachable from somewhere
        // other than this one worktree, or the commit that only lives on this branch goes with the folder.
        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);
        _Commit(record.Path, "only-here.txt", "work nobody else has a copy of\n");
        _BreakWorktreeGitLink(record.Path);
        _Git(_repo, "worktree", "unlock", record.Path);
        _Git(_repo, "worktree", "prune");

        var notice = await _manager.RemoveAsync(record);

        Assert.Empty((await _manager.ListAsync()));
        Assert.True(Directory.Exists(record.Path), "a commit that exists only on this branch must not be lost with the folder");
        Assert.NotNull(notice);
        Assert.DoesNotContain("deleted", notice);
        Assert.NotEmpty(_Git(_repo, "branch", "--list", "wt"));
    }

    [Fact]
    public async Task RemoveAsync_LeftoverFolderCouldNotBeProvenSafe_LogsWhyItWasLeftOnDisk()
    {
        var logger = Substitute.For<ILogger<WorktreeManager>>();
        using var manager = new WorktreeManager(new WorktreeRegistryStore(Path.Combine(_tempRoot, "logged.json")), _worktreesRoot, logger);
        var record = await manager.CreateAsync(_sessionId, "wt-logged", _repo);
        _BreakWorktreeGitLink(record.Path);
        File.WriteAllText(Path.Combine(record.Path, "left-behind.txt"), "not on any branch\n");
        _Git(_repo, "worktree", "unlock", record.Path);
        _Git(_repo, "worktree", "prune");

        await manager.RemoveAsync(record);

        logger.Received(1).Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(state => state!.ToString()!.Contains("could not be proven safe")),
            null,
            Arg.Any<Func<object, Exception?, string>>());
    }

    // Breaks a worktree's own recognition as a working tree without touching anything it checked out — unlike
    // _ClearCheckout, which wipes the whole folder. The tests above need the real, committed files to survive so
    // they can be compared against what the branch has, while git itself can no longer find them from inside the
    // folder any more (deleting record.Path/.git is exactly what a corrupted or manually-cleared worktree link
    // leaves behind).
    private static void _BreakWorktreeGitLink(string worktreePath)
    {
        var gitLink = Path.Combine(worktreePath, ".git");
        if (File.Exists(gitLink))
        {
            File.Delete(gitLink);
        }
        else if (Directory.Exists(gitLink))
        {
            Directory.Delete(gitLink, recursive: true);
        }
    }

    [Fact]
    public async Task ReattachAsync_RepositoryGone_StillReownsTheWorktreeInsteadOfThrowing()
    {
        // AC-507 defect 3: the re-lock used to run unguarded, so a git that could not even start (repository folder
        // gone) took the whole reattach down with it before the re-own — the part that actually has to land — ever
        // ran. A crashed session's worktree whose repository has since vanished must still be handed to the new
        // session, not left stuck on a misleading "is it installed and on PATH?" from a lock that was only ever
        // best-effort.
        var second = Path.Combine(_tempRoot, "second-repo");
        Directory.CreateDirectory(second);
        _Git(second, "init", "-b", "main");
        _Git(second, "config", "user.email", "test@example.com");
        _Git(second, "config", "user.name", "Test");
        _Commit(second, "README.md", "hello\n");
        var record = await _manager.CreateAsync(Guid.NewGuid().ToString("n"), "wt-second", second);
        TestGitDirectory.Remove(second);
        var newSession = Guid.NewGuid().ToString("n");
        _manager.Dispose();
        using var restartedCockpit = new WorktreeManager(new WorktreeRegistryStore(_configPath), _worktreesRoot);

        var reattached = await restartedCockpit.ReattachAsync(record.Path, newSession);

        Assert.NotNull(reattached);
        Assert.Equal(newSession, reattached!.SessionId);
        Assert.Equal(newSession, Assert.Single((await restartedCockpit.ListAsync())).SessionId);
    }

    [Fact]
    public async Task CreateAsync_TwoSessionsOnOneRepository_GiveEachOtherIsolatedTrees()
    {
        var first = await _manager.CreateAsync(Guid.NewGuid().ToString("n"), "cockpit/session-1", _repo);
        var second = await _manager.CreateAsync(Guid.NewGuid().ToString("n"), "cockpit/session-2", _repo);

        Assert.NotEqual(second.Path, first.Path);
        Assert.NotEqual(second.Branch, first.Branch);
        Assert.Equal(2, System.Linq.Enumerable.Count((await _manager.ListAsync())));

        File.WriteAllText(Path.Combine(first.Path, "only-in-first.txt"), "x\n");
        _Git(first.Path, "add", "-A");
        _Git(first.Path, "commit", "-m", "first-only work");

        Assert.False(File.Exists(Path.Combine(second.Path, "only-in-first.txt")));
    }

    [Fact]
    public async Task ReleaseAsync_CleanWorktree_RemovesItAndDeletesItsBranch()
    {
        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);

        await _manager.ReleaseAsync(_sessionId);

        Assert.False(Directory.Exists(record.Path));
        Assert.Empty((await _manager.ListAsync()));
        // Unlike a bare RemoveAsync, teardown of a clean worktree also deletes its (work-free) branch.
        Assert.Empty(_Git(_repo, "branch", "--list", "wt"));
    }

    [Fact]
    public async Task ReleaseAsync_WorktreeWithUncommittedWork_KeepsItAndMarksItRetained()
    {
        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);
        File.WriteAllText(Path.Combine(record.Path, "work.txt"), "unfinished\n");

        await _manager.ReleaseAsync(_sessionId);

        Assert.True(Directory.Exists(record.Path));
        var retained = Assert.Single((await _manager.ListAsync()));
        Assert.True(retained.IsRetained);
        Assert.Equal(record.Path, retained.Path);
        Assert.NotEmpty(_Git(_repo, "branch", "--list", "wt"));
    }

    [Fact]
    public async Task ReconcileAsync_RemovesAnOrphanedCleanWorktree_ButKeepsALiveOne()
    {
        var orphan = await _manager.CreateAsync(Guid.NewGuid().ToString("n"), "cockpit/orphan", _repo);
        var live = await _manager.CreateAsync(Guid.NewGuid().ToString("n"), "cockpit/live", _repo);
        _manager.Dispose();
        using var restartedCockpit = new WorktreeManager(new WorktreeRegistryStore(_configPath), _worktreesRoot);

        await restartedCockpit.ReconcileAsync([live.SessionId]);

        Assert.False(Directory.Exists(orphan.Path));
        Assert.True(Directory.Exists(live.Path));
        Assert.Equal(live.SessionId, Assert.Single((await restartedCockpit.ListAsync())).SessionId);
    }

    [Fact]
    public async Task GetStatusesAsync_ReportsClean_ThenDirty_ThenHoldingACommitThatExistsNowhereElse()
    {
        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);

        Assert.True(Assert.Single((await _manager.GetStatusesAsync())).IsClean);

        File.WriteAllText(Path.Combine(record.Path, "change.txt"), "work\n");
        var dirty = (await _manager.GetStatusesAsync()).Single();
        Assert.True(dirty.HasUncommittedChanges);
        Assert.False(dirty.IsClean);

        _Git(record.Path, "add", "-A");
        _Git(record.Path, "commit", "-m", "work");
        var holdingWork = (await _manager.GetStatusesAsync()).Single();
        Assert.Equal(1, holdingWork.StrandableCommits);
        Assert.False(holdingWork.IsClean);
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
        Assert.Equal(0, status.StrandableCommits);
        Assert.True(status.IsClean);
        Assert.True((await _manager.IsCleanAsync(record)));
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

        Assert.True((await _manager.IsCleanAsync(record)));
    }

    [Fact]
    public async Task IsCleanAsync_WorktreeWithACommitThatWasNeverPushed_IsNotClean()
    {
        // The guard on the rule above: a remote existing must not make everything read as safe. Only work that is
        // actually on it counts — this is the side where being wrong loses commits.
        _AddRemote();
        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);
        _Commit(record.Path, "change.txt", "work\n");

        Assert.False((await _manager.IsCleanAsync(record)));
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

        Assert.True((await _manager.IsCleanAsync(record)));
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

        Assert.True((await _manager.IsCleanAsync(record)));
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

        Assert.True((await _manager.IsCleanAsync(record)));
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

        Assert.True((await _manager.IsCleanAsync(legacy)));
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

        Assert.False((await _manager.IsCleanAsync(record)));
    }

    [Fact]
    public async Task IsCleanAsync_UnmergedWorkInAFileGitQuotes_IsNotClean()
    {
        // git renders a non-ASCII path as "caf\303\251.txt" — quoted and octal-escaped — and a pathspec built from
        // that text matches no file, which git reports as "no difference": the content check's safe-looking answer
        // for a branch whose work it never actually compared.
        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);
        _Commit(record.Path, "café.txt", "unmerged work\n");

        Assert.False((await _manager.IsCleanAsync(record)));
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

        Assert.False(Directory.Exists(record.Path));
        Assert.Contains("wt", _Git(_repo, "branch", "--list", "wt"));
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

        Assert.False(Directory.Exists(record.Path));
        Assert.Empty(_Git(_repo, "branch", "--list", "wt"));
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

        Assert.True((await _manager.IsCleanAsync(record)));
    }

    [Fact]
    public async Task ReattachAsync_ReassignsTheWorktreeToANewSession()
    {
        var record = await _manager.CreateAsync(Guid.NewGuid().ToString("n"), "cockpit/orphan", _repo);
        var newSession = Guid.NewGuid().ToString("n");
        _manager.Dispose();
        using var restartedCockpit = new WorktreeManager(new WorktreeRegistryStore(_configPath), _worktreesRoot);

        var reattached = await restartedCockpit.ReattachAsync(record.Path, newSession);

        Assert.NotNull(reattached);
        Assert.Equal(newSession, reattached!.SessionId);
        Assert.False(reattached.IsRetained);
        Assert.Equal(newSession, Assert.Single((await restartedCockpit.ListAsync())).SessionId);
    }

    [Fact]
    public async Task ReattachAsync_LiveLease_RefusesASecondWriterWithItsPathAndOwner()
    {
        var record = await _manager.CreateAsync(_sessionId, "cockpit/live", _repo);
        using var secondCockpit = new WorktreeManager(new WorktreeRegistryStore(_configPath), _worktreesRoot);

        var exception = await Assert.ThrowsAsync<WorktreeAdmissionException>(() =>
            secondCockpit.ReattachAsync(record.Path, Guid.NewGuid().ToString("n")));

        Assert.Equal(record.Path, exception.WorktreePath);
        Assert.Equal(_sessionId, exception.OwnerSessionId);
        Assert.Contains(record.Path, exception.Message);
        Assert.Contains(_sessionId, exception.Message);
        Assert.DoesNotContain("unknown", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReleaseOwnershipAsync_CaseVariantRecordPath_ReleasesTheLease()
    {
        _Git(_repo, "config", "core.ignorecase", "true");
        var record = await _manager.CreateAsync(_sessionId, "cockpit/case-release", _repo);
        var caseVariantPath = record.Path.ToUpperInvariant();
        Assert.False(string.Equals(record.Path, caseVariantPath, StringComparison.Ordinal));
        await _registry.AddAsync(record with { Path = caseVariantPath });

        await _manager.ReleaseOwnershipAsync(caseVariantPath);

        using var nextCockpit = new WorktreeManager(new WorktreeRegistryStore(_configPath), _worktreesRoot);
        var reattached = await nextCockpit.ReattachAsync(record.Path, Guid.NewGuid().ToString("n"));

        Assert.NotNull(reattached);
    }

    [Fact]
    public async Task TransferAsync_ExpectedOwner_AtomicallyHandsOverTheHeldLease()
    {
        var record = await _manager.CreateAsync(_sessionId, "cockpit/handover", _repo);

        var transferred = await _manager.TransferAsync(record.Path, _sessionId, "pane-target");
        var staleTransfer = await _manager.TransferAsync(record.Path, _sessionId, "pane-stale");

        Assert.Equal("pane-target", transferred?.SessionId);
        Assert.Null(staleTransfer);
        Assert.Equal("pane-target", Assert.Single(await _manager.ListAsync()).SessionId);
    }

    [Fact]
    public async Task ReattachAsync_SamePaneAfterProcessExit_ResumesSilently()
    {
        var record = await _manager.CreateAsync(_sessionId, "cockpit/restart", _repo);
        _manager.Dispose();
        using var restartedCockpit = new WorktreeManager(new WorktreeRegistryStore(_configPath), _worktreesRoot);

        var reattached = await restartedCockpit.ReattachAsync(record.Path, _sessionId);

        Assert.Equal(_sessionId, reattached?.SessionId);
    }

    [Fact]
    public async Task ReattachAsync_ConcurrentCockpits_AdmitExactlyOneWriter()
    {
        var record = await _manager.CreateAsync(_sessionId, "cockpit/race", _repo);
        await _manager.ReleaseOwnershipAsync(record.Path);
        using var firstCockpit = new WorktreeManager(new WorktreeRegistryStore(_configPath), _worktreesRoot);
        using var secondCockpit = new WorktreeManager(new WorktreeRegistryStore(_configPath), _worktreesRoot);

        var reattachments = await Task.WhenAll(
            _TryReattachAsync(firstCockpit, record.Path, "pane-first"),
            _TryReattachAsync(secondCockpit, record.Path, "pane-second"));

        Assert.Single(reattachments, record => record is not null);
    }

    [Fact]
    public async Task ReconcileAsync_LiveLease_SkipsTheWorktreeWithoutChangingItsOwner()
    {
        var record = await _manager.CreateAsync(_sessionId, "cockpit/reconcile-live", _repo);
        using var secondCockpit = new WorktreeManager(new WorktreeRegistryStore(_configPath), _worktreesRoot);

        await secondCockpit.ReconcileAsync([]);

        Assert.Equal(_sessionId, Assert.Single(await secondCockpit.ListAsync()).SessionId);
    }

    [Fact]
    public async Task ReattachAsync_UnknownPath_ReturnsNull()
    {
        var reattached = await _manager.ReattachAsync(Path.Combine(_tempRoot, "nope"), Guid.NewGuid().ToString("n"));

        Assert.Null(reattached);
    }

    [Fact]
    public async Task ReleaseOwnershipAsync_DropsTheSessionId_SoTheWorktreeReadsAsOrphaned()
    {
        // AC-520 fix 6: releasing does not remove anything — it only gives up the claim, so the record's owner no
        // longer matches any live session, and the row becomes an ordinary orphan (Remove/Reattach unblock from
        // there, the same as any other worktree whose session closed or crashed).
        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);

        await _manager.ReleaseOwnershipAsync(record.Path);

        var reloaded = Assert.Single(await _manager.ListAsync());
        Assert.NotEqual(_sessionId, reloaded.SessionId);
        Assert.True(Directory.Exists(record.Path));
    }

    [Fact]
    public async Task ReleaseOwnershipAsync_UnknownPath_IsANoOp()
    {
        await _manager.ReleaseOwnershipAsync(Path.Combine(_tempRoot, "nope"));

        Assert.Empty(await _manager.ListAsync());
    }

    [Fact]
    public async Task CreateAsync_PlacesTheWorktreeUnderTheConfiguredRootOverride()
    {
        var customRoot = Path.Combine(_tempRoot, "custom-worktree-root");
        var settings = Substitute.For<IWorktreeSettingsStore>();
        settings.LoadAsync(Arg.Any<CancellationToken>()).Returns(new WorktreeSettings { Root = customRoot });
        var manager = new WorktreeManager(new WorktreeRegistryStore(Path.Combine(_tempRoot, "override.json")), settings);

        var record = await manager.CreateAsync(_sessionId, "wt", _repo);

        Assert.StartsWith(Path.GetFullPath(customRoot), record.Path);
        Assert.True(Directory.Exists(record.Path));
    }

    [Fact]
    public async Task CreateAsync_SourceBranchBehindItsRemote_ForksFromTheUpdatedTip()
    {
        _AddRemote();
        var moved = _PushFromAnotherClone("shipped.txt");

        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);

        // The whole point of AC-349: the session starts on what the remote holds, not on what this checkout last
        // pulled — and the operator's own branch is carried along, so it is no longer behind either.
        Assert.Equal(moved, record.BaseCommit);
        Assert.Equal(moved, _Git(_repo, "rev-parse", "HEAD"));
        Assert.Equal(moved, _Git(record.Path, "rev-parse", "HEAD"));
        var refresh = _SourceRefreshOf(record);
        Assert.Equal(WorktreeSourceOutcome.FastForwarded, refresh.Outcome);
        Assert.Equal(1, refresh.BehindCount);
        Assert.Contains("origin/main", refresh.Notice);
    }

    [Fact]
    public async Task CreateAsync_TheSourceMayNotBeTouched_ForksFromTheUpstreamTipWithoutMovingTheBranch()
    {
        _AddRemote();
        var moved = _PushFromAnotherClone("shipped.txt");
        var before = _Git(_repo, "rev-parse", "HEAD");

        var record = await _manager.CreateAsync(_sessionId, "wt", _repo, WorktreeSourceHandling.LeaveSourceAlone);
        var refresh = _SourceRefreshOf(record);

        // The session still starts on the latest state — that is the whole point of AC-349 — but a folder an agent
        // merely named is not one to write to on its say-so. Same fork base, no branch moving under anyone.
        Assert.Equal(moved, record.BaseCommit);
        Assert.Equal(moved, _Git(record.Path, "rev-parse", "HEAD"));
        Assert.Equal(before, _Git(_repo, "rev-parse", "HEAD"));
        Assert.Equal(WorktreeSourceOutcome.ForkedFromUpstream, refresh.Outcome);
        Assert.Contains("left where it is", refresh.Notice);
    }

    [Fact]
    public async Task CreateAsync_TheSourceMayNotBeTouchedAndHasUncommittedChanges_StillForksFromTheUpstreamTip()
    {
        _AddRemote();
        var moved = _PushFromAnotherClone("shipped.txt");
        File.WriteAllText(Path.Combine(_repo, "README.md"), "half-finished edit\n");

        var record = await _manager.CreateAsync(_sessionId, "wt", _repo, WorktreeSourceHandling.LeaveSourceAlone);

        // An uncommitted edit is a reason not to *write* to the tree, and nothing is being written here — so it is
        // no reason to hand the session an older base than it could have had. The edit is untouched either way.
        Assert.Equal(moved, record.BaseCommit);
        Assert.Equal(WorktreeSourceOutcome.ForkedFromUpstream, _SourceRefreshOf(record).Outcome);
        Assert.Equal("half-finished edit\n", File.ReadAllText(Path.Combine(_repo, "README.md")));
    }

    [Fact]
    public async Task CreateAsync_TheSourceMayNotBeTouchedAndHasDivergedCommits_ForksFromTheLocalHead()
    {
        _AddRemote();
        _PushFromAnotherClone("shipped.txt");
        _Commit(_repo, "mine.txt", "not pushed yet\n");
        var before = _Git(_repo, "rev-parse", "HEAD");

        var record = await _manager.CreateAsync(_sessionId, "wt", _repo, WorktreeSourceHandling.LeaveSourceAlone);

        // Commits that exist only here belong in what the session starts from: forking from the upstream instead
        // would quietly hand the agent a base without the work that is actually being done.
        Assert.Equal(before, record.BaseCommit);
        Assert.Equal(WorktreeSourceOutcome.Diverged, _SourceRefreshOf(record).Outcome);
    }

    [Fact]
    public async Task CreateAsync_SourceBranchBehindWithUntrackedFilesOnly_StillForksFromTheUpdatedTip()
    {
        _AddRemote();
        var moved = _PushFromAnotherClone("shipped.txt");
        File.WriteAllText(Path.Combine(_repo, "scratch.log"), "build output\n");

        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);

        // Untracked leftovers are in nearly every checkout; counting them as work in progress would mean the source
        // is never updated, which is the feature. This one is nowhere near an incoming path, so nothing can touch it.
        Assert.Equal(moved, record.BaseCommit);
        var refresh = _SourceRefreshOf(record);
        Assert.Equal(WorktreeSourceOutcome.FastForwarded, refresh.Outcome);
        Assert.True(File.Exists(Path.Combine(_repo, "scratch.log")));
    }

    [Fact]
    public async Task CreateAsync_UntrackedFileBesideAnIncomingOne_DoesNotCountAsInTheWay()
    {
        _AddRemote();
        var moved = _PushFromAnotherClone("src/app/main.cs");
        Directory.CreateDirectory(Path.Combine(_repo, "src", "app"));
        File.WriteAllText(Path.Combine(_repo, "src", "app", "notes.txt"), "my scratch file\n");

        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);

        // Sharing a folder with an incoming file is not a collision — only the same path, or a folder standing where
        // one has to go, is. Counting neighbours would stop the update in any repository with a stray file in a
        // touched folder, which is nearly all of them: the feature would quietly never fire again.
        var refresh = _SourceRefreshOf(record);
        Assert.Equal(WorktreeSourceOutcome.FastForwarded, refresh.Outcome);
        Assert.Equal(moved, record.BaseCommit);
        Assert.Equal("my scratch file\n", File.ReadAllText(Path.Combine(_repo, "src", "app", "notes.txt")));
    }

    [Fact]
    public async Task CreateAsync_UntrackedFolderWhereAnIncomingFileMustGo_CountsAsInTheWay()
    {
        _AddRemote();
        _PushFromAnotherClone("libs");
        Directory.CreateDirectory(Path.Combine(_repo, "libs"));
        File.WriteAllText(Path.Combine(_repo, "libs", "mine.txt"), "not in git\n");
        var before = _Git(_repo, "rev-parse", "HEAD");

        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);

        // The other direction of the same question: here a folder of untracked files sits exactly where an incoming
        // file has to be written. git lists what is inside it, not the folder, so the match has to run upwards.
        var refresh = _SourceRefreshOf(record);
        Assert.Equal(WorktreeSourceOutcome.UntrackedFilesInTheWay, refresh.Outcome);
        Assert.Equal(before, _Git(_repo, "rev-parse", "HEAD"));
        Assert.Equal("not in git\n", File.ReadAllText(Path.Combine(_repo, "libs", "mine.txt")));
    }

    [Fact]
    public async Task CreateAsync_UpdateWouldOverwriteAnIgnoredFile_LeavesItAloneAndSaysSo()
    {
        _AddRemote();
        File.WriteAllText(Path.Combine(_repo, ".gitignore"), ".env\n");
        _Git(_repo, "add", "-A");
        _Git(_repo, "commit", "-m", "ignore .env");
        _Git(_repo, "push", "origin", "main");
        File.WriteAllText(Path.Combine(_repo, ".env"), "API_KEY=the-only-copy\n");
        _PushFromAnotherClone(".env");
        var before = _Git(_repo, "rev-parse", "HEAD");

        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);

        // git refuses to overwrite an untracked file but overwrites an *ignored* one without a word, and a local
        // .env is both the file that gets ignored and the one nobody has a second copy of. Asking about the incoming
        // paths ourselves is the only thing standing between an update and that content.
        Assert.Equal("API_KEY=the-only-copy\n", File.ReadAllText(Path.Combine(_repo, ".env")));
        Assert.Equal(before, _Git(_repo, "rev-parse", "HEAD"));
        Assert.Equal(before, record.BaseCommit);
        var refresh = _SourceRefreshOf(record);
        Assert.Equal(WorktreeSourceOutcome.UntrackedFilesInTheWay, refresh.Outcome);
        Assert.Contains(".env", refresh.Notice);
    }

    [Fact]
    public async Task CreateAsync_UpdateWouldOverwriteAnUntrackedFile_LeavesItAloneAndSaysSo()
    {
        _AddRemote();
        _PushFromAnotherClone("shipped.txt");
        File.WriteAllText(Path.Combine(_repo, "shipped.txt"), "mine, never committed\n");
        var before = _Git(_repo, "rev-parse", "HEAD");

        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);

        Assert.Equal("mine, never committed\n", File.ReadAllText(Path.Combine(_repo, "shipped.txt")));
        Assert.Equal(before, _Git(_repo, "rev-parse", "HEAD"));
        var refresh = _SourceRefreshOf(record);
        Assert.Equal(WorktreeSourceOutcome.UntrackedFilesInTheWay, refresh.Outcome);
    }

    [PosixFact("A colon cannot appear in a Windows filename, so the collision this is about cannot be built there.")]
    public async Task CreateAsync_UpdateWouldOverwriteAPathGitReadsAsPathspecMagic_LeavesItAlone()
    {
        _AddRemote();
        File.WriteAllText(Path.Combine(_repo, ".gitignore"), ":colon.txt\n");
        _Git(_repo, "add", "-A");
        _Git(_repo, "commit", "-m", "ignore it");
        _Git(_repo, "push", "origin", "main");
        File.WriteAllText(Path.Combine(_repo, ":colon.txt"), "the only copy\n");
        _PushFromAnotherClone(":colon.txt");

        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);

        // Handing these paths to git as a pathspec is what makes this one dangerous: a leading colon is read as
        // pathspec magic, the answer comes back empty, and "nothing in the way" is exactly the wrong conclusion.
        Assert.Equal("the only copy\n", File.ReadAllText(Path.Combine(_repo, ":colon.txt")));
        var refresh = _SourceRefreshOf(record);
        Assert.Equal(WorktreeSourceOutcome.UntrackedFilesInTheWay, refresh.Outcome);
    }

    [PosixFact("Creating a symlink on Windows needs privileges this test cannot assume.")]
    public async Task CreateAsync_UpdateWouldReplaceASymlinkedDirectory_LeavesItAlone()
    {
        _AddRemote();
        File.WriteAllText(Path.Combine(_repo, ".gitignore"), "libs\n");
        _Git(_repo, "add", "-A");
        _Git(_repo, "commit", "-m", "ignore libs");
        _Git(_repo, "push", "origin", "main");
        _PushFromAnotherClone("libs/dep.txt");
        Directory.CreateDirectory(Path.Combine(_tempRoot, "elsewhere"));
        File.WriteAllText(Path.Combine(_tempRoot, "elsewhere", "dep.txt"), "linked, not copied\n");
        Directory.CreateSymbolicLink(Path.Combine(_repo, "libs"), Path.Combine(_tempRoot, "elsewhere"));
        var before = _Git(_repo, "rev-parse", "HEAD");

        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);

        // Ignored on purpose: git refuses to replace an untracked symlink but replaces an ignored one without a
        // word, and it never descends into it either — so asking about "libs/dep.txt" finds nothing while the link
        // itself is what the update lands on. Without the check the operator's arrangement is simply gone.
        var refresh = _SourceRefreshOf(record);
        Assert.Equal(WorktreeSourceOutcome.UntrackedFilesInTheWay, refresh.Outcome);
        Assert.Equal(before, _Git(_repo, "rev-parse", "HEAD"));

        // What the update destroys is the link itself — it becomes a real folder with the incoming file in it, while
        // the directory it pointed at is left untouched. So the link is what has to be asserted on.
        Assert.NotNull(new DirectoryInfo(Path.Combine(_repo, "libs")).LinkTarget);
    }

    [Fact]
    public async Task CreateAsync_FastForwardRefused_ForksFromTheLocalHeadAndPassesOnWhatGitSaid()
    {
        _AddRemote();
        _PushFromAnotherClone("shipped.txt");
        var before = _Git(_repo, "rev-parse", "HEAD");

        // An index git cannot take is the one refusal that needs no co-operation from the fixture: everything up to
        // the merge succeeds, and the merge itself cannot start.
        File.WriteAllText(Path.Combine(_repo, ".git", "index.lock"), string.Empty);

        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);

        Assert.Equal(before, _Git(_repo, "rev-parse", "HEAD"));
        Assert.Equal(before, record.BaseCommit);
        var refresh = _SourceRefreshOf(record);
        Assert.Equal(WorktreeSourceOutcome.FastForwardFailed, refresh.Outcome);
        Assert.Contains("could not be updated", refresh.Notice);

        // The tree was never opened, so the operator must not be sent looking through it.
        Assert.DoesNotContain("now has changes in it", refresh.Notice);
    }

    [Fact]
    public async Task CreateAsync_UpdateWouldOverwriteAnIgnoredFileDifferingOnlyInCase_LeavesItAloneWhereGitSaysCaseDoesNotCount()
    {
        _AddRemote();
        File.WriteAllText(Path.Combine(_repo, ".gitignore"), "local.cfg\n");
        _Git(_repo, "add", "-A");
        _Git(_repo, "commit", "-m", "ignore it");
        _Git(_repo, "push", "origin", "main");

        // The filesystem under a repository does not have to agree with the operating system on it — a Linux
        // checkout on a CIFS share or a WSL-mounted Windows drive is case-insensitive all the same. git probes and
        // records the answer, so the check has to follow core.ignorecase rather than infer from the platform.
        _Git(_repo, "config", "core.ignorecase", "true");
        File.WriteAllText(Path.Combine(_repo, "local.cfg"), "the only copy\n");
        _PushFromAnotherClone("LOCAL.CFG");

        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);

        var refresh = _SourceRefreshOf(record);
        Assert.Equal(WorktreeSourceOutcome.UntrackedFilesInTheWay, refresh.Outcome);
        Assert.Equal("the only copy\n", File.ReadAllText(Path.Combine(_repo, "local.cfg")));
    }

    [PosixFact("Driven by a shell hook and a process-tree kill; both behave differently enough on Windows to prove something other than what this is about.")]
    [UnsupportedOSPlatform("windows")]
    public async Task BringUpToDateAsync_FastForwardKilledAfterTheBranchMoved_ReportsWhereItActuallyLanded()
    {
        _AddRemote();
        var moved = _PushFromAnotherClone("shipped.txt");
        var hook = Path.Combine(_repo, ".git", "hooks", "post-merge");
        File.WriteAllText(hook, "#!/bin/sh\nsleep 30\n");
        File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var repository = await _manager.DetectRepositoryAsync(_repo);

        var refresh = await WorktreeSourceUpdater.BringUpToDateAsync(_Detected(repository), WorktreeSourceHandling.BringUpToDate, CancellationToken.None, TimeSpan.FromSeconds(1));

        // git moves the branch before it runs the post-merge hook, so a hook that outlives the guard is killed with
        // the update already done. Believing the exit code there would report failure and then fork the session from
        // the commit the branch has just left — the very staleness this exists to prevent.
        Assert.Equal(moved, _Git(_repo, "rev-parse", "HEAD"));
        Assert.Equal(WorktreeSourceOutcome.FastForwarded, refresh.Outcome);
        Assert.Equal(moved, refresh.ForkCommit);
    }

    [PosixFact("Paced by a shell hook, which behaves differently enough on Windows to prove something else.")]
    [UnsupportedOSPlatform("windows")]
    public async Task BringUpToDateAsync_CancelledWhileTheFastForwardRuns_StillReportsWhatItDid()
    {
        _AddRemote();
        var moved = _PushFromAnotherClone("shipped.txt");
        var hook = Path.Combine(_repo, ".git", "hooks", "post-merge");
        File.WriteAllText(hook, "#!/bin/sh\nsleep 3\n");
        File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var repository = await _manager.DetectRepositoryAsync(_repo);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        var refresh = await WorktreeSourceUpdater.BringUpToDateAsync(_Detected(repository), WorktreeSourceHandling.BringUpToDate, cancellation.Token, TimeSpan.FromSeconds(30));

        // The merge deliberately ignores the caller's token, so by the time someone gives up the tree has already
        // been written to. Asking the repository what happened has to ignore it for the same reason: a caller who
        // walks away in this window would otherwise take the answer with them, leaving the update standing and
        // unreported while the start fails on the cancellation instead.
        Assert.Equal(moved, _Git(_repo, "rev-parse", "HEAD"));
        Assert.Equal(WorktreeSourceOutcome.FastForwarded, refresh.Outcome);
        Assert.Equal(moved, refresh.ForkCommit);
    }

    [PosixFact("Paced by a shell hook, which behaves differently enough on Windows to prove something else.")]
    [UnsupportedOSPlatform("windows")]
    public async Task CreateAsync_CancelledAfterTheBranchMoved_StillAnnouncesThatItMoved()
    {
        _AddRemote();
        var moved = _PushFromAnotherClone("shipped.txt");
        var hook = Path.Combine(_repo, ".git", "hooks", "post-merge");
        File.WriteAllText(hook, "#!/bin/sh\nsleep 3\n");
        File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var announced = new List<WorktreeSourceRefresh>();
        _manager.SourceRefreshed += announced.Add;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        var create = async () => await _manager.CreateAsync(_sessionId, "wt", _repo, cancellationToken: cancellation.Token);

        // The start is abandoned while the merge runs, so it never returns the record the notice used to travel on —
        // and by then the operator's own branch has already moved. Hearing about that cannot depend on a caller who
        // is no longer listening: a branch that moved without a word is the thing this whole feature is against.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(create);
        Assert.Equal(moved, _Git(_repo, "rev-parse", "HEAD"));
        var refresh = Assert.Single(announced);
        Assert.True(refresh.Outcome == WorktreeSourceOutcome.FastForwarded && refresh.Notice != null);
    }

    [Fact]
    public async Task CreateAsync_AnnouncementListenerThrows_StillMakesTheWorktree()
    {
        _AddRemote();
        _manager.SourceRefreshed += _ => throw new InvalidOperationException("a listener with problems of its own");

        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);

        // Telling someone is best-effort; making the worktree is not. A subscriber that falls over is its own
        // problem and must not become the reason a session cannot start.
        Assert.True(Directory.Exists(record.Path));
        Assert.Single((await _manager.ListAsync()));
    }

    [Fact]
    public async Task CreateAsync_CancelledBeforeItStarts_LeavesTheSourceBranchWhereItWas()
    {
        _AddRemote();
        _PushFromAnotherClone("shipped.txt");
        var before = _Git(_repo, "rev-parse", "HEAD");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var create = async () => await _manager.CreateAsync(_sessionId, "wt", _repo, cancellationToken: cancellation.Token);

        // The report of a move travels on the record this returns, and a caller who has given up never reads it. So
        // a start that is already cancelled must not move the branch at all — it would be a change nobody asked for
        // and nobody would hear about. Nothing enforces that on purpose: it holds because every step from detecting
        // the repository onwards runs on the caller's token, so an abandoned start stops while it is still reading.
        // Pinned here because that is a property of the whole path rather than of any one line in it.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(create);
        Assert.Equal(before, _Git(_repo, "rev-parse", "HEAD"));
    }

    [Fact]
    public async Task CreateAsync_FetchFailsAndTheUpstreamRefIsGone_StillReportsTheFailedFetch()
    {
        _AddRemote();
        var before = _Git(_repo, "rev-parse", "HEAD");
        _Git(_repo, "update-ref", "-d", "refs/remotes/origin/main");
        _Git(_repo, "remote", "set-url", "origin", Path.Combine(_tempRoot, "gone.git"));

        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);

        // The branch still tracks origin/main in config, but with the remote-tracking ref gone @{upstream} cannot be
        // resolved. Reading that as "this branch tracks nothing" would turn an unreachable remote into silence —
        // which is the exact blind spot this ticket is about.
        Assert.Equal(before, record.BaseCommit);
        var refresh = _SourceRefreshOf(record);
        Assert.Equal(WorktreeSourceOutcome.FetchFailed, refresh.Outcome);
        Assert.Contains("Could not reach", refresh.Notice);
    }

    [Fact]
    public async Task CreateAsync_SourceBranchBehindWithUncommittedChanges_LeavesTheWorkingTreeAlone()
    {
        _AddRemote();
        _PushFromAnotherClone("shipped.txt");
        var before = _Git(_repo, "rev-parse", "HEAD");
        File.WriteAllText(Path.Combine(_repo, "README.md"), "half-finished edit\n");

        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);

        Assert.Equal(before, _Git(_repo, "rev-parse", "HEAD"));
        Assert.Equal("half-finished edit\n", File.ReadAllText(Path.Combine(_repo, "README.md")));
        Assert.Equal(before, record.BaseCommit);
        var refresh = _SourceRefreshOf(record);
        Assert.Equal(WorktreeSourceOutcome.KeptLocalChanges, refresh.Outcome);
        Assert.Contains("uncommitted changes", refresh.Notice);
    }

    [Fact]
    public async Task CreateAsync_SourceBranchDivergedFromItsRemote_KeepsTheLocalCommits()
    {
        _AddRemote();
        _PushFromAnotherClone("shipped.txt");
        _Commit(_repo, "mine.txt", "not pushed yet\n");
        var before = _Git(_repo, "rev-parse", "HEAD");

        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);

        // Nothing that only exists here may be silently rewound; a fast-forward would not have been one anyway.
        Assert.Equal(before, _Git(_repo, "rev-parse", "HEAD"));
        Assert.Equal(before, record.BaseCommit);
        var refresh = _SourceRefreshOf(record);
        Assert.Equal(WorktreeSourceOutcome.Diverged, refresh.Outcome);
        Assert.Contains("left", refresh.Notice);
    }

    [Fact]
    public async Task CreateAsync_RemoteThatCannotBeReached_ForksFromTheLocalHeadAndSaysSo()
    {
        _AddRemote();
        _PushFromAnotherClone("shipped.txt");
        _Git(_repo, "fetch", "origin");
        var before = _Git(_repo, "rev-parse", "HEAD");
        _Git(_repo, "remote", "set-url", "origin", Path.Combine(_tempRoot, "gone.git"));

        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);

        // The remote-tracking ref still says "one behind", but nothing confirmed that just now — so the session
        // starts on the local head rather than on a claim about the past, and it is told which.
        Assert.Equal(before, _Git(_repo, "rev-parse", "HEAD"));
        Assert.Equal(before, record.BaseCommit);
        var refresh = _SourceRefreshOf(record);
        Assert.Equal(WorktreeSourceOutcome.FetchFailed, refresh.Outcome);
        Assert.Contains("Could not reach", refresh.Notice);
    }

    [Fact]
    public async Task CreateAsync_SourceBranchAlreadyOnTheRemoteTip_SaysNothing()
    {
        _AddRemote();

        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);

        var refresh = _SourceRefreshOf(record);
        Assert.Equal(WorktreeSourceOutcome.UpToDate, refresh.Outcome);
        Assert.Null(refresh.Notice);
    }

    [Fact]
    public async Task CreateAsync_UpstreamBranchDeletedOnTheRemote_SaysNothing()
    {
        _AddRemote();
        _Git(_repo, "checkout", "-b", "feature");
        _Git(_repo, "push", "-u", "origin", "feature");
        _PushFromAnotherClone("shipped.txt");
        _Git(_tempRoot, "clone", "--branch", "main", _RemotePath, Path.Combine(_tempRoot, "closer"));
        _Git(Path.Combine(_tempRoot, "closer"), "push", "origin", "--delete", "feature");
        _Git(_repo, "fetch", "--prune", "origin");

        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);

        // A merged-and-deleted branch still carries its tracking config, but the ref it points at is gone. There is
        // nothing left to be behind of, so this must stay as quiet as any other branch without an upstream —
        // otherwise every session started from a finished feature branch opens with a warning about nothing.
        var refresh = _SourceRefreshOf(record);
        Assert.Equal(WorktreeSourceOutcome.NoUpstream, refresh.Outcome);
        Assert.Null(refresh.Notice);
    }

    [Fact]
    public async Task CreateAsync_UnreachableRemoteSpelledAsACredentialledUrl_KeepsTheCredentialOutOfWhatItSays()
    {
        _AddRemote();
        _Git(_repo, "config", "branch.main.remote", "https://someone:s3cr3t-token@127.0.0.1:1/repo.git");

        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);
        var refresh = _SourceRefreshOf(record);

        // git takes a URL where a remote's name would go, and a URL can carry a token. This sentence reaches a toast
        // and, through the worktree tool, an agent's context — so not one character of it may be the token.
        Assert.Equal(WorktreeSourceOutcome.FetchFailed, refresh.Outcome);
        Assert.NotNull(refresh.Notice);
        Assert.DoesNotContain("s3cr3t-token", refresh.Notice);
        Assert.DoesNotContain("someone", refresh.Notice);
    }

    [Fact]
    public async Task BringUpToDateAsync_ConfigItCannotRead_SaysSoRatherThanNothing()
    {
        _AddRemote();
        var repository = _Detected(await _manager.DetectRepositoryAsync(_repo));
        File.AppendAllText(Path.Combine(_repo, ".git", "config"), "\n[[[not a section\n");

        var refresh = await WorktreeSourceUpdater.BringUpToDateAsync(repository, WorktreeSourceHandling.BringUpToDate, CancellationToken.None);

        // "I could not tell" and "there is nothing to tell" are the same silence if you let them be, and only one of
        // them is honest. A config git refuses to parse is the former.
        Assert.Equal(WorktreeSourceOutcome.CheckFailed, refresh.Outcome);
        Assert.NotNull(refresh.Notice);
    }

    [Fact]
    public async Task CreateAsync_RepositoryWithoutARemote_SaysNothing()
    {
        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);

        Assert.Equal(_Git(_repo, "rev-parse", "HEAD"), record.BaseCommit);
        var refresh = _SourceRefreshOf(record);
        Assert.Equal(WorktreeSourceOutcome.NoUpstream, refresh.Outcome);
        Assert.Null(refresh.Notice);
    }

    [Fact]
    public async Task CreateAsync_DetachedHeadBehindOrigin_ForksFromOriginInstead()
    {
        // AC-1218: a shared checkout left detached on a commit origin has since moved past — the state a worktree
        // spawned from it silently inherited as its base, with nothing telling anyone it happened.
        _AddRemote();
        var pushedAt = _PushFromAnotherClone("shipped.txt");
        var detachedAt = _Git(_repo, "rev-parse", "HEAD");
        _Git(_repo, "checkout", detachedAt);

        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);

        // Forked from origin's current tip, not the stale commit HEAD was detached at — and the checkout itself
        // never moved (still detached at the old commit, nothing written to it).
        Assert.Equal(pushedAt, record.BaseCommit);
        Assert.Equal(detachedAt, _Git(_repo, "rev-parse", "HEAD"));
        var refresh = _SourceRefreshOf(record);
        Assert.Equal(WorktreeSourceOutcome.ForkedFromUpstream, refresh.Outcome);
        Assert.NotNull(refresh.Notice);
    }

    [Fact]
    public async Task CreateAsync_DetachedHeadAtOriginTip_LeavesTheSourceUntouched()
    {
        // Negative control for AC-1218: a detached checkout that already matches origin must not change behaviour.
        _AddRemote();
        var detachedAt = _Git(_repo, "rev-parse", "HEAD");
        _Git(_repo, "checkout", detachedAt);

        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);

        Assert.Equal(detachedAt, record.BaseCommit);
        var refresh = _SourceRefreshOf(record);
        Assert.Equal(WorktreeSourceOutcome.UpToDate, refresh.Outcome);
        Assert.Null(refresh.Notice);
    }

    [Fact]
    public async Task CreateAsync_DetachedHeadNoRemote_LeavesTheSourceUntouched()
    {
        var detachedAt = _Git(_repo, "rev-parse", "HEAD");
        _Git(_repo, "checkout", detachedAt);

        var record = await _manager.CreateAsync(_sessionId, "wt", _repo);

        // Nothing to compare the detached commit against, so it stays the fork base exactly as before AC-1218.
        Assert.Equal(detachedAt, record.BaseCommit);
        var refresh = _SourceRefreshOf(record);
        Assert.Equal(WorktreeSourceOutcome.DetachedHead, refresh.Outcome);
        Assert.Null(refresh.Notice);
    }

    private static async Task<WorktreeRecord?> _TryReattachAsync(WorktreeManager manager, string path, string paneId)
    {
        try
        {
            return await manager.ReattachAsync(path, paneId);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _manager.Dispose();
        TestGitDirectory.Remove(_tempRoot);
    }

    private string _RemotePath => Path.Combine(_tempRoot, "remote.git");

    // The two shapes this file kept reaching for a null-forgiving "!" to express. Failing the test with a sentence
    // beats suppressing the compiler: when one of these is unexpectedly null, the message says which and why.
    private static WorktreeSourceRefresh _SourceRefreshOf(WorktreeRecord record) =>
        record.SourceRefresh ?? throw new InvalidOperationException("the record carries no source refresh.");

    private static GitRepositoryInfo _Detected(GitRepositoryInfo? repository) =>
        repository ?? throw new InvalidOperationException("the fixture folder was not detected as a git repository.");

    /// <summary>
    /// Pushes one more commit to origin from a second clone and returns its sha — someone else moving the branch on
    /// while this checkout stays where it was, which is the state the whole feature is about.
    /// </summary>
    private string _PushFromAnotherClone(string file)
    {
        var elsewhere = Path.Combine(_tempRoot, $"elsewhere-{Guid.NewGuid():n}");
        // --branch main explicitly: a bare repository initialised here keeps its own idea of HEAD, so a plain clone
        // can land on a branch that does not exist and the push would have nothing to send.
        _Git(_tempRoot, "clone", "--branch", "main", _RemotePath, elsewhere);
        _Git(elsewhere, "config", "user.email", "other@example.com");
        _Git(elsewhere, "config", "user.name", "Other");

        // --force on the add so a path the repository ignores can still be the incoming change: the file that lands
        // on top of an ignored local one is exactly the case worth a fixture. ":(literal)" because a path starting
        // with a colon is otherwise read as pathspec magic — the same trap the code under test has to survive.
        var target = Path.Combine(elsewhere, file);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "shipped elsewhere\n");
        _Git(elsewhere, "add", "--force", "--", $":(literal){file}");
        _Git(elsewhere, "commit", "-m", $"work on {file}");
        _Git(elsewhere, "push", "origin", "main");

        return _Git(elsewhere, "rev-parse", "HEAD");
    }

    /// <summary>A bare repository as origin, with main already on it — the "has somewhere to be pushed to" fixture.</summary>
    private void _AddRemote()
    {
        var remote = _RemotePath;
        _Git(_tempRoot, "init", "--bare", remote);
        _Git(_repo, "remote", "add", "origin", remote);
        _Git(_repo, "push", "-u", "origin", "main");
    }

    /// <summary>
    /// Empties a worktree folder without removing it — the checkout, including the <c>.git</c> file that makes it a
    /// worktree, gone while the folder stays. What a removal that cleared the tree and then could not delete the
    /// directory leaves behind, and the shape of the leftovers found in a real state directory.
    /// </summary>
    private static void _ClearCheckout(string worktreePath)
    {
        TestGitDirectory.Remove(worktreePath);
        Directory.CreateDirectory(worktreePath);
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
