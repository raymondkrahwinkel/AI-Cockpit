using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Core.Worktrees;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Worktrees;

// <inheritdoc cref="IWorktreeManager" />
internal sealed class WorktreeManager : IWorktreeManager, ISingletonService
{
    // Cap on the readable branch fragment in a folder name, so a long branch cannot push a Windows worktree path past its limit.
    private const int SlugLength = 32;

    private static readonly StringComparison PathComparison = GitPaths.PlatformComparison;

    private readonly IWorktreeRegistry _registry;
    private readonly Func<CancellationToken, Task<string>> _resolveRoot;
    private readonly ILogger<WorktreeManager>? _logger;

    public event Action<WorktreeSourceRefresh>? SourceRefreshed;

    public WorktreeManager(IWorktreeRegistry registry, IWorktreeSettingsStore settings, ILogger<WorktreeManager>? logger = null)
    {
        _registry = registry;
        _logger = logger;

        // Resolved per create, so an override the operator changes in Options takes effect on the next worktree
        // rather than only on a restart. A blank override keeps the default under the app state root. An unreadable
        // config must never make creating a worktree fail — resolving the root is not the place to surface a corrupt
        // cockpit.json — so a load failure falls back to the default root rather than throwing on the create path.
        _resolveRoot = async cancellationToken =>
        {
            string? root;
            try
            {
                root = (await settings.LoadAsync(cancellationToken).ConfigureAwait(false)).Root;
            }
            catch (Exception)
            {
                return CockpitConfigPath.WorktreesRoot;
            }

            return string.IsNullOrWhiteSpace(root) ? CockpitConfigPath.WorktreesRoot : Path.GetFullPath(root);
        };
    }

    // Test seam: place the worktrees under an arbitrary fixed root instead of the app state directory.
    internal WorktreeManager(IWorktreeRegistry registry, string worktreesRoot, ILogger<WorktreeManager>? logger = null)
    {
        _registry = registry;
        _resolveRoot = _ => Task.FromResult(worktreesRoot);
        _logger = logger;
    }

    public async Task<GitRepositoryInfo?> DetectRepositoryAsync(string directory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        var insideWorkTree = await GitCli.RunAsync(directory, ["rev-parse", "--is-inside-work-tree"], cancellationToken).ConfigureAwait(false);
        if (insideWorkTree.ExitCode != 0 || insideWorkTree.StandardOutput.Trim() != "true")
        {
            return null;
        }

        // A repository with no commit yet has no HEAD to branch from; that is "cannot isolate", the same answer the
        // dialog wants for a non-repository, so it collapses to null rather than throwing at spawn time.
        var head = await GitCli.RunAsync(directory, ["rev-parse", "HEAD"], cancellationToken).ConfigureAwait(false);
        if (head.ExitCode != 0)
        {
            return null;
        }

        var root = await GitCli.RunCheckedAsync(directory, ["rev-parse", "--show-toplevel"], cancellationToken).ConfigureAwait(false);
        var branch = await GitCli.RunCheckedAsync(directory, ["rev-parse", "--abbrev-ref", "HEAD"], cancellationToken).ConfigureAwait(false);

        return new GitRepositoryInfo(
            Path.GetFullPath(root),
            head.StandardOutput.Trim(),
            branch.Equals("HEAD", StringComparison.Ordinal) ? null : branch);
    }

    public async Task<WorktreeRecord> CreateAsync(
        string sessionId,
        string branch,
        string directory,
        WorktreeSourceHandling handling = WorktreeSourceHandling.BringUpToDate,
        bool isAgentCreated = false,
        CancellationToken cancellationToken = default)
    {
        var repository = await DetectRepositoryAsync(directory, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"'{directory}' is not inside a git repository with a commit, so it cannot be isolated in a worktree.");

        // Fork from the latest the source branch can safely be brought to, not from whatever the operator's checkout
        // last pulled (AC-349). Best-effort: everything this cannot do — offline, a dirty tree, a diverged branch —
        // ends as the fork-from-local-HEAD this always did, with a sentence saying so.
        var sourceRefresh = await WorktreeSourceUpdater.BringUpToDateAsync(repository, handling, cancellationToken).ConfigureAwait(false);
        if (sourceRefresh.ForkCommit is { } forkAt)
        {
            repository = repository with { HeadCommit = forkAt };
        }

        // Announced here rather than through the returned record, because from this line on the operator's checkout
        // may already have moved and everything below can still fail: a cancelled start, a branch name that is
        // taken, a git that will not run. Any of those throws, and a record nobody receives cannot tell anyone their
        // branch is not where they left it. A listener that throws must not take the creation down with it.
        try
        {
            SourceRefreshed?.Invoke(sourceRefresh);
        }
        catch (Exception)
        {
            // Telling someone is best-effort; making the worktree is not.
        }

        var worktreesRoot = await _resolveRoot(cancellationToken).ConfigureAwait(false);
        var worktreePath = _ResolveWorktreePath(worktreesRoot, repository.Root, sessionId, branch);

        // The worktree must never live inside the repository it checks out — that pollutes the working tree it is
        // meant to keep clean and risks git tracking its own worktree. The state root is always elsewhere, so this
        // guards a test seam and a future caller more than the production path, but it fails closed either way.
        if (_IsInside(worktreePath, repository.Root))
        {
            throw new InvalidOperationException(
                $"Refusing to create a worktree at '{worktreePath}' — it is inside the repository at '{repository.Root}'.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(worktreePath)!);

        // -b, never -B: an already-existing branch is a hard failure, not a silent reset of that branch's history
        // onto a new base. --lock holds the worktree against a prune sweep for as long as the session owns it.
        // Submodules are not auto-populated here: `git worktree add` has no --recurse-submodules option (verified
        // against git 2.55, contrary to the design note), so a repository that uses submodules needs a
        // `git submodule update --init` inside the worktree — a documented limitation, not a common case.
        await GitCli.RunCheckedAsync(
            repository.Root,
            ["worktree", "add", "--lock", "--reason", $"cockpit session {sessionId}", "-b", branch, worktreePath, repository.HeadCommit],
            cancellationToken).ConfigureAwait(false);

        var record = new WorktreeRecord(
            sessionId,
            repository.Root,
            Path.GetFullPath(worktreePath),
            branch,
            repository.HeadCommit,
            DateTimeOffset.UtcNow)
        {
            // The branch we forked from — measured against its moving tip later so a merged worktree reads clean.
            // Null when HEAD was detached at creation; the status check falls back to the repository's default branch.
            BaseBranch = repository.CurrentBranch,
            IsAgentCreated = isAgentCreated,
        };
        await _registry.AddAsync(record, cancellationToken).ConfigureAwait(false);

        // Carried on the returned record, not persisted: the caller that started this session is the one that tells
        // the operator where it forked from, and after that the answer is history.
        return record with { SourceRefresh = sourceRefresh };
    }

    public Task<WorktreeRecord> CreateForSessionAsync(
        string sessionId,
        string? sessionLabel,
        string directory,
        WorktreeSourceHandling handling = WorktreeSourceHandling.BringUpToDate,
        bool isAgentCreated = false,
        CancellationToken cancellationToken = default) =>
        CreateAsync(sessionId, _BuildBranchName(sessionLabel, sessionId), directory, handling, isAgentCreated, cancellationToken);

    public Task<IReadOnlyList<WorktreeRecord>> ListAsync(CancellationToken cancellationToken = default) =>
        _registry.ListAsync(cancellationToken);

    public async Task<IReadOnlyList<WorktreeStatus>> GetStatusesAsync(CancellationToken cancellationToken = default)
    {
        // Each worktree's status is several independent git subprocesses (a porcelain status plus resolving its base
        // ref and counting unmerged commits); run them across worktrees at once so opening the panel costs the slowest
        // tree rather than the sum of all of them. Order is preserved (Task.WhenAll keeps it).
        var records = await _registry.ListAsync(cancellationToken).ConfigureAwait(false);
        return await Task.WhenAll(records.Select(record => _StatusOfAsync(record, cancellationToken))).ConfigureAwait(false);
    }

    private static async Task<WorktreeStatus> _StatusOfAsync(WorktreeRecord record, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(record.Path))
        {
            return new WorktreeStatus(record, Exists: false, HasUncommittedChanges: false, StrandableCommits: 0);
        }

        try
        {
            var status = await GitCli.RunCheckedAsync(record.Path, ["status", "--porcelain"], cancellationToken).ConfigureAwait(false);
            var strandable = await _StrandableCommitCountAsync(record, cancellationToken).ConfigureAwait(false);

            return new WorktreeStatus(
                record,
                Exists: true,
                HasUncommittedChanges: status.Length > 0,
                StrandableCommits: strandable);
        }
        catch (Exception)
        {
            // The folder is there but git could not answer from inside it. Which of the two reasons it is decides
            // everything downstream, so ask: a folder that is no longer a working tree at all — emptied by a removal
            // that could not delete it, its administration since reclaimed by a prune — holds no working copy to
            // lose, and saying "uncommitted changes" about it is a claim about work nobody can point at. It also
            // makes the row unsweepable and its Remove button permanently refusable, which is the state this reads
            // its way out of (the sibling of AC-342, with the folder left behind instead of gone).
            if (!await _HasWorkingCopyAsync(record.Path, cancellationToken).ConfigureAwait(false))
            {
                return new WorktreeStatus(record, Exists: true, HasUncommittedChanges: false, StrandableCommits: 0)
                {
                    WorkingCopyMissing = true,
                };
            }

            // A working copy git cannot read (corrupt, mid-delete). Report it as holding changes: a status we cannot
            // confirm is treated as not-clean, so the panel never invites a remove that might lose work it could not
            // see.
            return new WorktreeStatus(record, Exists: true, HasUncommittedChanges: true, StrandableCommits: 0);
        }
    }

    // Whether a git working copy is still at <paramref name="path"/> — git's own answer, not an inference from what
    // the folder holds. The state this exists for: the folder is on disk but the checkout inside it is not, so every
    // question asked from within it fails, and the pessimistic reading of that failure ("it might hold work") keeps a
    // worktree that demonstrably holds none. A git that cannot be run at all counts as "there is one": an answer we
    // could not get is not evidence the checkout is gone, and every caller is safer keeping a worktree than forgetting
    // one.
    private static async Task<bool> _HasWorkingCopyAsync(string path, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(path))
        {
            return false;
        }

        try
        {
            var inside = await GitCli.RunAsync(path, ["rev-parse", "--is-inside-work-tree"], cancellationToken).ConfigureAwait(false);
            return inside.ExitCode == 0 && inside.StandardOutput.Trim().Equals("true", StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return true;
        }
    }

    public async Task<bool> IsCleanAsync(WorktreeRecord record, CancellationToken cancellationToken = default)
    {
        if (await _PorcelainDirtyAsync(record.Path, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        return await _StrandableCommitCountAsync(record, cancellationToken).ConfigureAwait(false) == 0;
    }

    // The work a removal would strand: commits that exist nowhere but in this worktree's branch (AC-266). The
    // question is not "is this branch merged" but "can removing it lose anything", and a commit is safe the moment
    // it is reachable from somewhere else — so this asks three cheap questions and stops at the first "safe":
    //
    //   1. Reachable from the base branch's current tip, or from ANY remote-tracking ref. One rev-list does both.
    //      The remote half is what makes a pushed-but-unmerged branch removable: a push updates the local
    //      remote-tracking ref, so this needs no network and is true the instant the session pushed.
    //   2. Present in the base by content rather than by identity — a squash- or rebase-merge rewrites the commits,
    //      so their SHAs are absent from the base while the work is in it. `git cherry` compares patch ids.
    //   3. Same content on the files this branch touched. This is what catches a squash of SEVERAL commits, which
    //      step 2 misses: patch ids are per commit, and one squashed commit matches none of the originals.
    //
    // Every step errs towards "still holds work" — the direction that keeps a tree rather than losing one. Both the
    // panel status and the teardown clean-gate share this, so the two never disagree on what "has work to keep" means.
    private static Task<int> _StrandableCommitCountAsync(WorktreeRecord record, CancellationToken cancellationToken) =>
        _CommitsOutsideBaseAsync(record, treatPushedAsSafe: true, cancellationToken);

    // Whether every commit on this branch is in the base branch itself — the stricter question, with a push to a
    // remote NOT counting as an answer. Deleting the local branch is gated on this rather than on IsCleanAsync: a
    // remote-tracking ref is a claim about a remote as this repository last saw it, and a force-push or a deleted
    // remote branch makes that claim stale. Keeping the branch costs a dead ref; getting it wrong costs the commits.
    private static async Task<bool> _IsFullyInBaseAsync(WorktreeRecord record, CancellationToken cancellationToken) =>
        await _CommitsOutsideBaseAsync(record, treatPushedAsSafe: false, cancellationToken).ConfigureAwait(false) == 0;

    // Both existing callers measure the worktree's own HEAD from inside it; the leftover-folder safety check below
    // (RemoveAsync) cannot do that — the folder is precisely the one git no longer recognises as a working tree —
    // so it measures the branch by name from the repository root instead. Same question, two vantage points.
    private static Task<int> _CommitsOutsideBaseAsync(WorktreeRecord record, bool treatPushedAsSafe, CancellationToken cancellationToken) =>
        _CommitsOutsideBaseAsync(record, record.Path, "HEAD", treatPushedAsSafe, cancellationToken);

    private static async Task<int> _CommitsOutsideBaseAsync(WorktreeRecord record, string gitContextPath, string tip, bool treatPushedAsSafe, CancellationToken cancellationToken)
    {
        var baseRef = await _ResolveBaseRefAsync(record with { Path = gitContextPath }, cancellationToken).ConfigureAwait(false);
        List<string> arguments = ["rev-list", "--count", tip, "--not", baseRef];
        if (treatPushedAsSafe)
        {
            arguments.Add("--remotes");
        }

        var raw = await GitCli.RunCheckedAsync(gitContextPath, arguments, cancellationToken).ConfigureAwait(false);
        if (!int.TryParse(raw, out var count) || count == 0)
        {
            return 0;
        }

        return await _IsInBaseByContentAsync(gitContextPath, tip, baseRef, cancellationToken).ConfigureAwait(false) ? 0 : count;
    }

    // Whether the base already holds this branch's work under different commits — the squash/rebase/cherry-pick case,
    // where comparing history by identity says "unmerged" about work that is demonstrably in the base.
    private static async Task<bool> _IsInBaseByContentAsync(string path, string tip, string baseRef, CancellationToken cancellationToken)
    {
        // '+' marks a commit whose patch the base does not have; none of them means every commit arrived, however it
        // was rewritten on the way. Skipped when a merge commit is among them: git cherry compares patches and emits
        // no line at all for a merge, so a branch whose only unmerged commit IS a merge — an evil merge carrying
        // conflict resolution of its own — would read as "all present" on an empty answer.
        var merges = await GitCli.RunAsync(path, ["rev-list", "--count", "--merges", tip, "--not", baseRef], cancellationToken).ConfigureAwait(false);
        var hasNoMergeCommit = merges.ExitCode == 0 && merges.StandardOutput.Trim() == "0";

        var cherry = await GitCli.RunAsync(path, ["cherry", baseRef, tip], cancellationToken).ConfigureAwait(false);
        if (hasNoMergeCommit
            && cherry.ExitCode == 0
            && !cherry.StandardOutput.Split('\n').Any(line => line.StartsWith('+')))
        {
            return true;
        }

        // Several commits squashed into one: no per-commit patch matches, so ask the only question that still holds —
        // do the files this branch touched look exactly the same in the base? If they do, there is nothing to strand.
        // If the base moved on past the merge, they differ and the tree is kept: a false "has work", never a false
        // "safe". Paths come from the fork point (three-dot), so files only the base changed are not consulted, and
        // -z because git quotes and octal-escapes a non-ASCII path by default — a pathspec that then matches nothing,
        // which git answers with "no difference" and would read as safe.
        var touched = await GitCli.RunAsync(path, ["diff", "--name-only", "-z", $"{baseRef}...{tip}"], cancellationToken).ConfigureAwait(false);
        var paths = touched.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (touched.ExitCode != 0 || paths.Length == 0)
        {
            return false;
        }

        var difference = await GitCli.RunAsync(
            path,
            ["diff", "--quiet", baseRef, tip, "--", .. paths],
            cancellationToken).ConfigureAwait(false);

        return difference.ExitCode == 0;
    }

    // The ref to measure "unmerged" against: the base branch's current tip, so a merged worktree reads as clean. The
    // first candidate git can resolve to a commit wins (a recorded base branch since deleted is skipped), and the
    // frozen fork commit is the last resort so this never throws — it just falls back to the old ahead-of-fork count.
    private static async Task<string> _ResolveBaseRefAsync(WorktreeRecord record, CancellationToken cancellationToken)
    {
        // The common, current-format case: the branch we forked from is recorded. Resolve it and stop, so the panel's
        // per-worktree fan-out never spends the default-branch discovery below on every tree it already knows the
        // base of.
        var recordedBase = record.BaseBranch;
        if (!string.IsNullOrWhiteSpace(recordedBase)
            && await _ResolvesToCommitAsync(record.Path, recordedBase, cancellationToken).ConfigureAwait(false))
        {
            return await _FurthestKnownTipAsync(record.Path, recordedBase, cancellationToken).ConfigureAwait(false);
        }

        // Legacy records (written before the base branch was tracked) and detached-HEAD creations have no recorded
        // branch: fall back to the repository's default branch. Discover its name from origin/HEAD but prefer the
        // LOCAL ref of that name, so a worktree merged into a local main that has not been pushed yet still reads as
        // merged rather than being measured against a stale origin tip. Only if no local ref matches do we measure
        // against the remote-tracking ref itself, and only then against the frozen fork commit.
        var originHead = await GitCli.RunAsync(
            record.Path,
            ["symbolic-ref", "--short", "refs/remotes/origin/HEAD"],
            cancellationToken).ConfigureAwait(false);
        var remoteDefault = originHead.ExitCode == 0 ? originHead.StandardOutput.Trim() : string.Empty;

        var candidates = new List<string>();
        if (remoteDefault.StartsWith("origin/", StringComparison.Ordinal))
        {
            candidates.Add(remoteDefault["origin/".Length..]);
        }

        candidates.Add("main");
        candidates.Add("master");

        if (remoteDefault.Length > 0)
        {
            candidates.Add(remoteDefault);
        }

        foreach (var candidate in candidates.Distinct(StringComparer.Ordinal))
        {
            if (await _ResolvesToCommitAsync(record.Path, candidate, cancellationToken).ConfigureAwait(false))
            {
                return await _FurthestKnownTipAsync(record.Path, candidate, cancellationToken).ConfigureAwait(false);
            }
        }

        return record.BaseCommit;
    }

    // The base branch as far along as this repository knows it: its local tip, or its remote-tracking tip when the
    // local one has not caught up. Measuring against a local branch that lags behind the remote reports work as
    // unmerged that the merge on the remote already absorbed — an operator who never pulls would keep every finished
    // worktree forever. Only ever moves FORWARD: a local branch that is ahead (merged locally, not yet pushed) wins,
    // which is what the local-first preference protected.
    private static async Task<string> _FurthestKnownTipAsync(string path, string branch, CancellationToken cancellationToken)
    {
        // git's own answer to "where does this branch push to", rather than guessing at origin/<branch>: it honours a
        // second remote, a differently-named upstream, and a branch whose name has slashes of its own.
        var upstream = await GitCli.RunAsync(
            path,
            ["rev-parse", "--abbrev-ref", $"{branch}@{{upstream}}"],
            cancellationToken).ConfigureAwait(false);

        var tracking = upstream.StandardOutput.Trim();
        if (upstream.ExitCode != 0 || tracking.Length == 0)
        {
            return branch;
        }

        var ancestorCheck = await GitCli.RunAsync(
            path,
            ["merge-base", "--is-ancestor", branch, tracking],
            cancellationToken).ConfigureAwait(false);

        return ancestorCheck.ExitCode == 0 ? tracking : branch;
    }

    // Whether git can peel <paramref name="reference"/> to a commit from within the worktree — the gate that keeps a
    // candidate that no longer exists (a deleted base branch) or is not a commit out of the count measurement.
    private static async Task<bool> _ResolvesToCommitAsync(string path, string reference, CancellationToken cancellationToken)
    {
        var verify = await GitCli.RunAsync(
            path,
            ["rev-parse", "--verify", "--quiet", $"{reference}^{{commit}}"],
            cancellationToken).ConfigureAwait(false);

        return verify.ExitCode == 0;
    }

    public Task<bool> HasUncommittedChangesAsync(WorktreeRecord record, CancellationToken cancellationToken = default) =>
        _PorcelainDirtyAsync(record.Path, cancellationToken);

    // The porcelain "does the working tree still hold uncommitted changes or untracked files" check — the exact
    // content a force-remove would discard — shared by the teardown clean-gate and the agent-facing dirty-removal
    // consent gate so the rule lives in one place. A folder that is gone holds nothing; a folder git cannot read
    // (corrupt, mid-delete) is treated as holding changes, the safe direction — a state we cannot prove clean is
    // never silently discarded.
    private static async Task<bool> _PorcelainDirtyAsync(string path, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(path))
        {
            return false;
        }

        try
        {
            var status = await GitCli.RunCheckedAsync(path, ["status", "--porcelain"], cancellationToken).ConfigureAwait(false);
            return status.Length > 0;
        }
        catch (Exception)
        {
            // Unreadable because there is no working copy left in the folder — nothing to discard, so nothing dirty.
            // Unreadable for any other reason is the corrupt/mid-delete case above: treated as holding changes.
            return await _HasWorkingCopyAsync(path, cancellationToken).ConfigureAwait(false);
        }
    }

    // Returns a notice for the caller to surface when the removal succeeded but left something on disk the operator
    // should know about — null on a plain removal, with nothing left behind to mention.
    public async Task<string?> RemoveAsync(WorktreeRecord record, bool force = false, CancellationToken cancellationToken = default)
    {
        if (Directory.Exists(record.RepositoryRoot))
        {
            var refusal = await _AskGitToRemoveAsync(record, force, cancellationToken).ConfigureAwait(false);
            if (refusal is not null)
            {
                _logger?.LogInformation(
                    "git refused to remove worktree '{Branch}' at '{Path}': {Refusal}", record.Branch, record.Path, refusal);
            }

            // A refusal about a folder that still holds a working copy stands: it may hold work, and git said in its
            // own words why it would not go. With no working copy left there is nothing for git to remove — the
            // folder gone after a manual delete plus a prune, or a folder left behind with the checkout cleared out
            // of it — and the registry entry is the only thing that outlived the worktree. Dropping that entry IS the
            // removal then. Failing instead would leave the panel a row whose Remove button can never succeed
            // (AC-342), and git's own admin entry, if one lingers, is what the reconcile sweep's prune is for.
            if (refusal is not null && await _HasWorkingCopyAsync(record.Path, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(refusal);
            }
        }
        // Else (AC-507): the repository this worktree was forked from is gone — `git worktree remove` cannot run
        // without it (there is nowhere to ask), so it is never even attempted. Route A (Raymond, 2026-07-30): drop
        // the registry entry unconditionally and leave the worktree folder exactly as it is, whether or not it still
        // holds uncommitted work — the alternative (keep refusing) reproduces AC-342, a Remove button that can never
        // succeed.

        // Whichever branch above got here is about to drop the registry entry as the only removal that is still
        // possible — git either was never asked (repository gone) or already agreed there is nothing left it can
        // measure. "Removed from the cockpit" must still mean "gone from disk" whenever that is provable (Raymond:
        // a worktree Cockpit shows as removed must actually be gone), so a folder that still holds content is only
        // ever left in place when deleting it cannot be shown to be safe — never on a guess (cleanup-policy A).
        string? notice = null;
        try
        {
            if (Directory.Exists(record.Path) && Directory.EnumerateFileSystemEntries(record.Path).Any())
            {
                if (await _CanDeleteLeftoverFolderAsync(record, cancellationToken).ConfigureAwait(false))
                {
                    try
                    {
                        Directory.Delete(record.Path, recursive: true);
                        notice =
                            $"'{record.Branch}' could not be handed back to git, so its worktree folder at '{record.Path}' was " +
                            "checked by hand: everything left in it was already safely committed on the branch, so the folder " +
                            "itself was deleted.";
                        _logger?.LogInformation(
                            "Deleted leftover worktree folder '{Path}' for branch '{Branch}': its content matched what the branch already has committed.",
                            record.Path, record.Branch);
                    }
                    catch (Exception)
                    {
                        notice = _LeftOnDiskNotice(record);
                        _logger?.LogWarning(
                            "Leftover worktree folder '{Path}' for branch '{Branch}' was provably safe to delete but the delete itself failed; left on disk.",
                            record.Path, record.Branch);
                    }
                }
                else
                {
                    notice = _LeftOnDiskNotice(record);
                    _logger?.LogInformation(
                        "Leftover worktree folder '{Path}' for branch '{Branch}' could not be proven safe to delete; left on disk.",
                        record.Path, record.Branch);
                }
            }
        }
        catch (Exception)
        {
            // An unreadable folder (permissions, a dying mount) is still a folder that might hold something —
            // the same safe direction _PorcelainDirtyAsync and _HasWorkingCopyAsync take elsewhere in this file.
            // Dropping the entry must never depend on being able to enumerate what is left behind: silence here
            // would have reintroduced exactly the undeletable row this fix exists to remove.
            notice = $"'{record.Branch}''s worktree folder at '{record.Path}' could not be checked and was left on disk, untouched.";
            _logger?.LogWarning(
                "Could not even check what is left in '{Path}' for branch '{Branch}'; left on disk, untouched.", record.Path, record.Branch);
        }

        await _registry.RemoveAsync(record.Path, cancellationToken).ConfigureAwait(false);
        _TryRemoveIfEmpty(record.Path);
        _TryRemoveIfEmpty(Path.GetDirectoryName(record.Path));
        return notice;
    }

    private static string _LeftOnDiskNotice(WorktreeRecord record) =>
        $"'{record.Branch}' could not be handed back to git and was only dropped from the list. " +
        $"Its worktree folder was left on disk at '{record.Path}' and is no longer managed by the cockpit. " +
        "If its repository only became unavailable temporarily (an unmounted drive, for example), this " +
        "worktree was created locked, so it will not be reclaimed automatically once the repository " +
        "comes back — run 'git worktree unlock' and 'git worktree prune' there by hand.";

    // Whether a leftover worktree folder holds nothing beyond what a commit already safely preserves elsewhere, so
    // deleting it destroys no work — the hard boundary Fix 2 exists to enforce: physical deletion only ever follows
    // proof, never a guess (cleanup-policy A). Two questions, both answered from the repository root rather than the
    // folder itself — that folder is precisely the one git no longer recognises as a working tree, which is why
    // RemoveAsync is asking this at all:
    //   1. Is every commit reachable only from this branch already reachable from the base or a remote?
    //   2. Does every file physically on disk match, byte for byte, what that branch's own tip has committed?
    // A "no", or an "I cannot tell" to either — the repository itself gone, a branch that no longer resolves, a rev-
    // list that fails — answers false: left on disk is the safe fallback either way.
    private static async Task<bool> _CanDeleteLeftoverFolderAsync(WorktreeRecord record, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(record.RepositoryRoot))
        {
            // Nowhere to ask anything — the repository itself is what is gone (AC-507's own case), which must never
            // be treated as "provably safe" by default.
            return false;
        }

        try
        {
            var strandedElsewhere = await _CommitsOutsideBaseAsync(
                record, record.RepositoryRoot, record.Branch, treatPushedAsSafe: true, cancellationToken).ConfigureAwait(false);
            if (strandedElsewhere != 0)
            {
                return false;
            }

            return await _MatchesTrackedContentAsync(record, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // git could not answer one of the two questions above (the branch no longer resolves, a rev-list
            // failed) — the same "cannot prove, so do not touch it" fallback every other cleanup-policy-A check in
            // this file takes.
            return false;
        }
    }

    // Whether every real file still sitting under the folder is exactly what the branch's own tip already
    // committed — answered by comparing git's own tree listing to the files on disk, since the folder's local git
    // recognition is gone (that is why this is being asked at all, rather than a plain `git status`). Any extra
    // file, any content mismatch, or a folder that cannot even be walked (permissions, a dying mount) all read as
    // "not provably safe": the cost of a false "safe" is somebody's lost work, the cost of a false "not sure" is
    // only a folder left behind for the operator to look at by hand.
    private static async Task<bool> _MatchesTrackedContentAsync(WorktreeRecord record, CancellationToken cancellationToken)
    {
        var tracked = await _TrackedBlobsAsync(record.RepositoryRoot, record.Branch, cancellationToken).ConfigureAwait(false);
        if (tracked is null)
        {
            return false;
        }

        List<string> onDisk;
        try
        {
            onDisk = Directory.EnumerateFiles(record.Path, "*", SearchOption.AllDirectories)
                .Where(file => !_IsGitLinkArtifact(record.Path, file))
                .ToList();
        }
        catch (Exception)
        {
            return false;
        }

        foreach (var file in onDisk)
        {
            var relative = Path.GetRelativePath(record.Path, file).Replace(Path.DirectorySeparatorChar, '/');
            if (!tracked.TryGetValue(relative, out var blobSha))
            {
                return false;
            }

            var hash = await GitCli.RunAsync(record.RepositoryRoot, ["hash-object", file], cancellationToken).ConfigureAwait(false);
            if (hash.ExitCode != 0 || !string.Equals(hash.StandardOutput.Trim(), blobSha, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    // The branch's own tracked files, by path, at its current tip — null when the branch itself no longer resolves
    // to a commit (deleted, or never existed), which leaves nothing to compare the folder's content against.
    private static async Task<Dictionary<string, string>?> _TrackedBlobsAsync(string repositoryRoot, string branch, CancellationToken cancellationToken)
    {
        if (!await _ResolvesToCommitAsync(repositoryRoot, branch, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var listing = await GitCli.RunAsync(repositoryRoot, ["ls-tree", "-r", "-z", branch], cancellationToken).ConfigureAwait(false);
        if (listing.ExitCode != 0)
        {
            return null;
        }

        var blobs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in listing.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = entry.IndexOf('\t');
            if (tab < 0)
            {
                continue;
            }

            // "<mode> <type> <sha>" before the tab; only a blob (a file) is a candidate here — a submodule's
            // "commit" entry has nothing on disk in this folder that hash-object could ever match.
            var metadata = entry[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (metadata.Length == 3 && metadata[1] == "blob")
            {
                blobs[entry[(tab + 1)..]] = metadata[2];
            }
        }

        return blobs;
    }

    // The worktree's own dangling git-link file — the very thing broken here, which is why the folder needed asking
    // about at all — is not "work" and is excluded from the comparison above rather than counted as an untracked
    // file that blocks the delete.
    private static bool _IsGitLinkArtifact(string worktreePath, string filePath) =>
        Path.GetRelativePath(worktreePath, filePath).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0]
            .Equals(".git", StringComparison.Ordinal);

    // Asks git to remove the worktree and reports what it refused with, or null when it went through. Unlocked
    // first because git declines a locked worktree without a second --force; that unlock may itself fail (already
    // unlocked after a prune elsewhere, or a manual git) and is deliberately ignored — the removal is the step that
    // has to land, and it speaks for itself. git being unrunnable here at all (the repository folder is gone) is a
    // refusal like any other: the caller decides what it means, knowing whether the worktree is still on disk.
    private static async Task<string?> _AskGitToRemoveAsync(WorktreeRecord record, bool force, CancellationToken cancellationToken)
    {
        string[] arguments = force
            ? ["worktree", "remove", "--force", record.Path]
            : ["worktree", "remove", record.Path];

        try
        {
            await GitCli.RunAsync(record.RepositoryRoot, ["worktree", "unlock", record.Path], cancellationToken).ConfigureAwait(false);
            var removal = await GitCli.RunAsync(record.RepositoryRoot, arguments, cancellationToken).ConfigureAwait(false);

            return removal.ExitCode == 0 ? null : GitCli.DescribeFailure(removal);
        }
        catch (InvalidOperationException exception)
        {
            return exception.Message;
        }
    }

    // The empty folders a removal leaves behind: the worktree's own, when git would not delete it (a removal that got
    // as far as clearing the checkout, and a Windows handle on the directory that outlasted it), and the
    // per-repository grouping folder (<worktreesRoot>/<repo-hash>/) above it, which git never touches — it removes
    // the worktree leaf, not the folder holding it. Sweep both so finished repositories do not accumulate empty
    // directories. Best-effort and only when empty: a folder still holding anything — a sibling worktree, files left
    // in a cleared-out one — is never touched.
    private static void _TryRemoveIfEmpty(string? directory)
    {
        try
        {
            if (directory is not null && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch (Exception)
        {
            // An empty folder we could not remove is untidy, not dangerous.
        }
    }

    public async Task<WorktreeRecord?> ReattachAsync(string worktreePath, string newSessionId, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(worktreePath);
        var records = await _registry.ListAsync(cancellationToken).ConfigureAwait(false);
        var existing = records.FirstOrDefault(record => string.Equals(Path.GetFullPath(record.Path), fullPath, PathComparison));
        if (existing is null)
        {
            return null;
        }

        // Re-lock so a reconcile sweep leaves the reattached worktree alone, and re-own it so liveness and later
        // teardown follow the new session rather than the dead one. Locking is best-effort — it may already be
        // locked, or the repository behind it may be gone entirely (AC-507) — and the try/catch makes that true in
        // practice as well as in the comment: an unhandled start failure here (GitCli now throws instead of
        // silently returning a non-zero exit) previously took the whole reattach down with it, before the re-own
        // below — the part that actually has to land — ever ran.
        var locked = true;
        try
        {
            await GitCli.RunAsync(
                existing.RepositoryRoot,
                ["worktree", "lock", "--reason", $"cockpit session {newSessionId}", existing.Path],
                cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // git could not even start against this repository root — nothing to lock, so nothing to re-own is
            // blocked by it. The record says so honestly (IsLocked = false) rather than claiming a lock that never
            // landed; a later reconcile sweep may prune it, which is no worse than today.
            locked = false;
        }

        var reattached = existing with { SessionId = newSessionId, IsRetained = false, IsLocked = locked };
        await _registry.AddAsync(reattached, cancellationToken).ConfigureAwait(false);

        return reattached;
    }

    public async Task ReleaseOwnershipAsync(string worktreePath, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(worktreePath);
        var records = await _registry.ListAsync(cancellationToken).ConfigureAwait(false);
        if (records.FirstOrDefault(record => string.Equals(Path.GetFullPath(record.Path), fullPath, PathComparison)) is not { } existing)
        {
            return;
        }

        // Only the owner is given up — no git call, no removal. What that makes possible: the record reads as an
        // ordinary orphan everywhere else that matters (the reconcile sweep, the MCP remove guard's liveness check),
        // without this method having to know or duplicate either of their rules.
        await _registry.AddAsync(existing with { SessionId = string.Empty }, cancellationToken).ConfigureAwait(false);
    }

    public async Task ReleaseAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var records = await _registry.ListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var record in records.Where(record => string.Equals(record.SessionId, sessionId, StringComparison.Ordinal)))
        {
            await _ReleaseOneAsync(record, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ReconcileAsync(IReadOnlyCollection<string> liveSessionIds, CancellationToken cancellationToken = default)
    {
        var records = await _registry.ListAsync(cancellationToken).ConfigureAwait(false);
        var orphans = records.Where(record => !liveSessionIds.Contains(record.SessionId)).ToList();
        _logger?.LogInformation(
            "Reconcile sweep: {OrphanCount} of {TotalCount} registered worktrees belong to no live session.",
            orphans.Count, records.Count);

        foreach (var record in orphans)
        {
            try
            {
                await _ReleaseOneAsync(record, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // One orphan that will not release (a stale lock, a folder still held) must not abort the sweep and
                // strand every remaining orphan across restarts — skip it; the next reconcile retries it. This runs as
                // a fire-and-forget at startup, so a throw here would also be an unobserved task exception.
                _logger?.LogWarning(exception, "Reconcile sweep could not release orphaned worktree '{Path}'; left for the next sweep.", record.Path);
            }
        }

        // Reclaim git's own admin entries for worktrees whose folder disappeared out from under it (a manual delete),
        // which a plain registry drop cannot — done per repository the registry still knows about.
        foreach (var repositoryRoot in records.Select(record => record.RepositoryRoot).Distinct(StringComparer.Ordinal))
        {
            try
            {
                await GitCli.RunAsync(repositoryRoot, ["worktree", "prune"], cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A repository that itself vanished cannot be pruned; the registry drop above already forgot its
                // worktrees, so there is nothing left to leak.
            }
        }
    }

    private async Task _ReleaseOneAsync(WorktreeRecord record, CancellationToken cancellationToken)
    {
        // A worktree with no working copy left — its folder gone, or the folder still there with the checkout cleared
        // out of it — has nothing to keep, and nothing about it can be measured either: the clean check below runs git
        // inside that folder and fails, which counts as "not clean" and marks the record retained — so teardown left
        // exactly the entry the panel then could not remove (AC-342). Drop it here instead. The branch is kept, as on
        // every other removal, so commits that live only there stay reachable.
        if (!await _HasWorkingCopyAsync(record.Path, cancellationToken).ConfigureAwait(false))
        {
            await RemoveAsync(record, force: false, cancellationToken).ConfigureAwait(false);
            return;
        }

        bool clean;
        try
        {
            clean = await IsCleanAsync(record, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // If we cannot tell (the worktree folder is gone, git errors), treat it as not clean: keeping a worktree
            // that might hold work is the safe direction (cleanup-policy A never destroys work on a guess).
            clean = false;
        }

        if (clean)
        {
            // Asked while the worktree is still there, since the answer is measured from inside it. The branch goes
            // only when its work is in the base branch itself, otherwise finished sessions would pile up branches
            // nobody merges. Deliberately stricter than the clean-gate above (AC-266): that one also calls a pushed
            // branch safe, which it is for the working tree — a checkout is reproducible — but not a reason to drop
            // the local commits, since the remote-tracking ref proving it may be stale.
            bool isWorkInTheBase;
            try
            {
                isWorkInTheBase = await _IsFullyInBaseAsync(record, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                isWorkInTheBase = false;
            }

            await RemoveAsync(record, force: false, cancellationToken).ConfigureAwait(false);

            // Best-effort: the worktree, the thing that shared the working tree, is already gone; a branch git
            // declines to delete is not worth failing on.
            if (isWorkInTheBase)
            {
                await GitCli.RunAsync(record.RepositoryRoot, ["branch", "-d", "--", record.Branch], cancellationToken).ConfigureAwait(false);
            }
        }
        else if (!record.IsRetained)
        {
            // Keep the work and mark it retained, so the worktree panel shows it for review and no sweep auto-removes
            // it (cleanup-policy A). Idempotent: an already-retained record is left as it is.
            await _registry.AddAsync(record with { IsRetained = true }, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string _ResolveWorktreePath(string worktreesRoot, string repositoryRoot, string sessionId, string branch)
    {
        // Grouped per repository (a short stable hash of its root) so one repository's worktrees stay together and a
        // `git worktree list` cleanup is simple; the leaf carries a readable branch fragment plus the session id, so
        // two sessions on the same repository never collide.
        var repositoryFolder = _ShortHash(repositoryRoot);
        var slug = _Slug(branch);
        var shortId = _ShortId(sessionId);
        var leaf = slug.Length > 0 ? $"{slug}-{shortId}" : shortId;

        return Path.GetFullPath(Path.Combine(worktreesRoot, repositoryFolder, leaf));
    }

    // No ticket is bound to a session at start yet, so the branch is a readable slug plus the session's own short id
    // (§10.5.4) — the id, not a timestamp, because two sessions started in the same second under one label would
    // otherwise collide on the name and git's -b would refuse the second. Ticket-based naming lands when a session
    // carries a linked ticket.
    private static string _BuildBranchName(string? sessionLabel, string sessionId)
    {
        var slug = _Slug(sessionLabel ?? string.Empty);
        return $"cockpit/{(slug.Length > 0 ? slug : "session")}-{_ShortId(sessionId)}";
    }

    private static string _ShortHash(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    private static string _ShortId(string sessionId)
    {
        var compact = new string(sessionId.Where(char.IsLetterOrDigit).Take(8).ToArray());
        return compact.Length > 0 ? compact : _ShortHash(sessionId);
    }

    private static string _Slug(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString().Trim('-');
        return slug.Length > SlugLength ? slug[..SlugLength].Trim('-') : slug;
    }

    private static bool _IsInside(string candidate, string parent)
    {
        var candidateFull = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parentFull = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(candidateFull, parentFull, PathComparison))
        {
            return true;
        }

        var relative = Path.GetRelativePath(parentFull, candidateFull);
        return relative != "." && !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }
}
