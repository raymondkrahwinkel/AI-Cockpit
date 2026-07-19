using System.Security.Cryptography;
using System.Text;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Core.Worktrees;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Worktrees;

/// <inheritdoc cref="IWorktreeManager" />
internal sealed class WorktreeManager : IWorktreeManager, ISingletonService
{
    /// <summary>Cap on the readable branch fragment in a folder name, so a long branch cannot push a Windows worktree path past its limit.</summary>
    private const int SlugLength = 32;

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly IWorktreeRegistry _registry;
    private readonly Func<CancellationToken, Task<string>> _resolveRoot;

    public WorktreeManager(IWorktreeRegistry registry, IWorktreeSettingsStore settings)
    {
        _registry = registry;

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

    /// <summary>Test seam: place the worktrees under an arbitrary fixed root instead of the app state directory.</summary>
    internal WorktreeManager(IWorktreeRegistry registry, string worktreesRoot)
    {
        _registry = registry;
        _resolveRoot = _ => Task.FromResult(worktreesRoot);
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

    public async Task<WorktreeRecord> CreateAsync(string sessionId, string branch, string directory, CancellationToken cancellationToken = default)
    {
        var repository = await DetectRepositoryAsync(directory, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"'{directory}' is not inside a git repository with a commit, so it cannot be isolated in a worktree.");

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
            DateTimeOffset.UtcNow);
        await _registry.AddAsync(record, cancellationToken).ConfigureAwait(false);

        return record;
    }

    public Task<WorktreeRecord> CreateForSessionAsync(string sessionId, string? sessionLabel, string directory, CancellationToken cancellationToken = default) =>
        CreateAsync(sessionId, _BuildBranchName(sessionLabel, sessionId), directory, cancellationToken);

    public Task<IReadOnlyList<WorktreeRecord>> ListAsync(CancellationToken cancellationToken = default) =>
        _registry.ListAsync(cancellationToken);

    public async Task<IReadOnlyList<WorktreeStatus>> GetStatusesAsync(CancellationToken cancellationToken = default)
    {
        // Each worktree's status is two independent git subprocesses; run them across worktrees at once so opening the
        // panel costs the slowest tree rather than the sum of all of them. Order is preserved (Task.WhenAll keeps it).
        var records = await _registry.ListAsync(cancellationToken).ConfigureAwait(false);
        return await Task.WhenAll(records.Select(record => _StatusOfAsync(record, cancellationToken))).ConfigureAwait(false);
    }

    private static async Task<WorktreeStatus> _StatusOfAsync(WorktreeRecord record, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(record.Path))
        {
            return new WorktreeStatus(record, Exists: false, HasUncommittedChanges: false, CommitsAhead: 0);
        }

        try
        {
            var status = await GitCli.RunCheckedAsync(record.Path, ["status", "--porcelain"], cancellationToken).ConfigureAwait(false);
            var aheadRaw = await GitCli.RunCheckedAsync(record.Path, ["rev-list", "--count", $"{record.BaseCommit}..HEAD"], cancellationToken).ConfigureAwait(false);

            return new WorktreeStatus(
                record,
                Exists: true,
                HasUncommittedChanges: status.Length > 0,
                CommitsAhead: int.TryParse(aheadRaw, out var ahead) ? ahead : 0);
        }
        catch (Exception)
        {
            // The folder is there but git cannot read it (corrupt, mid-delete). Report it as holding changes: a
            // status we cannot confirm is treated as not-clean, so the panel never invites a remove that might lose
            // work it could not see.
            return new WorktreeStatus(record, Exists: true, HasUncommittedChanges: true, CommitsAhead: 0);
        }
    }

    public async Task<bool> IsCleanAsync(WorktreeRecord record, CancellationToken cancellationToken = default)
    {
        var status = await GitCli.RunCheckedAsync(record.Path, ["status", "--porcelain"], cancellationToken).ConfigureAwait(false);
        if (status.Length > 0)
        {
            return false;
        }

        var aheadOfBase = await GitCli.RunCheckedAsync(
            record.Path,
            ["rev-list", "--count", $"{record.BaseCommit}..HEAD"],
            cancellationToken).ConfigureAwait(false);

        return aheadOfBase == "0";
    }

    public async Task RemoveAsync(WorktreeRecord record, bool force = false, CancellationToken cancellationToken = default)
    {
        // Unlock first — git refuses to remove a locked worktree without a second --force. It may already be
        // unlocked (a prune elsewhere, a manual git), which git reports as a non-zero we deliberately ignore: the
        // removal is the step that has to succeed, and it says so itself if it cannot.
        await GitCli.RunAsync(record.RepositoryRoot, ["worktree", "unlock", record.Path], cancellationToken).ConfigureAwait(false);

        string[] arguments = force
            ? ["worktree", "remove", "--force", record.Path]
            : ["worktree", "remove", record.Path];
        await GitCli.RunCheckedAsync(record.RepositoryRoot, arguments, cancellationToken).ConfigureAwait(false);

        await _registry.RemoveAsync(record.Path, cancellationToken).ConfigureAwait(false);
        _TryRemoveEmptyParentDirectory(record.Path);
    }

    // The per-repository grouping folder (<worktreesRoot>/<repo-hash>/) is left behind empty once its last worktree
    // is removed; git removes the worktree leaf, not the folder above it. Sweep it so finished repositories do not
    // accumulate empty directories. Best-effort and only when empty — never touches a folder still holding a sibling.
    private static void _TryRemoveEmptyParentDirectory(string worktreePath)
    {
        try
        {
            var parent = Path.GetDirectoryName(worktreePath);
            if (parent is not null && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
            {
                Directory.Delete(parent);
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
        // locked; the re-own is the part that has to land.
        await GitCli.RunAsync(
            existing.RepositoryRoot,
            ["worktree", "lock", "--reason", $"cockpit session {newSessionId}", existing.Path],
            cancellationToken).ConfigureAwait(false);

        var reattached = existing with { SessionId = newSessionId, IsRetained = false, IsLocked = true };
        await _registry.AddAsync(reattached, cancellationToken).ConfigureAwait(false);

        return reattached;
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

        foreach (var record in records.Where(record => !liveSessionIds.Contains(record.SessionId)))
        {
            try
            {
                await _ReleaseOneAsync(record, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // One orphan that will not release (a stale lock, a folder still held) must not abort the sweep and
                // strand every remaining orphan across restarts — skip it; the next reconcile retries it. This runs as
                // a fire-and-forget at startup, so a throw here would also be an unobserved task exception.
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
            await RemoveAsync(record, force: false, cancellationToken).ConfigureAwait(false);

            // A clean worktree's branch carries no unique work — it is still at its base — so its branch goes too,
            // otherwise finished sessions would pile up branches nobody merges. Best-effort: the worktree, the thing
            // that shared the working tree, is already gone; a branch git declines to delete is not worth failing on.
            await GitCli.RunAsync(record.RepositoryRoot, ["branch", "-d", record.Branch], cancellationToken).ConfigureAwait(false);
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
