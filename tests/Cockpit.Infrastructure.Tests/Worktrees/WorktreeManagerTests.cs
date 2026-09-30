using System.Diagnostics;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Core.Worktrees;
using Cockpit.Infrastructure.Worktrees;
using Cockpit.TestSupport;

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
    public async Task CreateAsync_BranchThatAlreadyExists_FailsLoudly_WithoutResettingIt()
    {
        const string branch = "already-here";
        _Git(_repo, "branch", branch);

        var create = async () => await _manager.CreateAsync(_sessionId, branch, _repo);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(create);
        Assert.Contains("already exists", ex.Message);
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

    public static IEnumerable<object[]> WorktreesChangedMutations()
    {
        yield return new object[]
        {
            (Func<IWorktreeManager, string, string, Task<WorktreeRecord?>>)((_, _, _) => Task.FromResult<WorktreeRecord?>(null)),
            (Func<IWorktreeManager, string, WorktreeRecord?, string, Task>)(async (manager, repo, _, sessionId) =>
                await manager.CreateAsync(sessionId, "wt-create", repo)),
        };
        yield return new object[]
        {
            (Func<IWorktreeManager, string, string, Task<WorktreeRecord?>>)(async (manager, repo, sessionId) =>
                await manager.CreateAsync(sessionId, "wt-remove", repo)),
            (Func<IWorktreeManager, string, WorktreeRecord?, string, Task>)(async (manager, _, record, _) =>
                await manager.RemoveAsync(record!)),
        };
        yield return new object[]
        {
            // Reattach admits a new writer only once the old one let go — the same release/reattach sequence
            // ReleaseOwnershipAsync_CaseVariantRecordPath_ReleasesTheLease already exercises above.
            (Func<IWorktreeManager, string, string, Task<WorktreeRecord?>>)(async (manager, repo, sessionId) =>
            {
                var created = await manager.CreateAsync(sessionId, "wt-reattach", repo);
                await manager.ReleaseOwnershipAsync(created.Path);
                return created;
            }),
            (Func<IWorktreeManager, string, WorktreeRecord?, string, Task>)(async (manager, _, record, _) =>
                await manager.ReattachAsync(record!.Path, Guid.NewGuid().ToString("N"))),
        };
        yield return new object[]
        {
            (Func<IWorktreeManager, string, string, Task<WorktreeRecord?>>)(async (manager, repo, sessionId) =>
                await manager.CreateAsync(sessionId, "wt-transfer", repo)),
            (Func<IWorktreeManager, string, WorktreeRecord?, string, Task>)(async (manager, _, record, sessionId) =>
                await manager.TransferAsync(record!.Path, sessionId, Guid.NewGuid().ToString("N"))),
        };
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
