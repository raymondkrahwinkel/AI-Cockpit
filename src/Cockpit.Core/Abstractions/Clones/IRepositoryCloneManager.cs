using Cockpit.Core.Clones;

namespace Cockpit.Core.Abstractions.Clones;

/// <summary>
/// Clones a git repository from a URL into a managed area and hands back the local path a session starts in (AC-90),
/// so an agent can be put on a repository that is not yet on this machine. Composes with worktree isolation (AC-85):
/// the clone is the repository root, and each session then worktrees off it. Host-first: a generic session-launch
/// capability, not a per-provider one.
/// </summary>
/// <remarks>
/// Authentication is deliberately left to the host's own git credential helper (GCM, <c>gh</c>): a private HTTPS
/// repository clones without the cockpit ever touching a secret. The clone always runs with terminal prompting
/// disabled, so a missing helper fails with a message rather than hanging on an invisible prompt. A token is never
/// put in the URL — it would land in <c>.git/config</c>, the process arguments and the logs.
/// </remarks>
public interface IRepositoryCloneManager
{
    /// <summary>
    /// Clones the repository at <paramref name="url"/> into the managed clones area and returns its record, or —
    /// when the same repository is already cloned there — reuses the existing checkout (fetching it up to date)
    /// rather than cloning it again. Throws with what git said when the clone fails (authentication, an unreachable
    /// host, a bad URL), or when the target slug is already occupied by a <em>different</em> repository, which is
    /// never silently clobbered.
    /// </summary>
    Task<RepositoryClone> CloneAsync(string url, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RepositoryClone>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reconciles the registry at startup (AC-90): a record whose folder is gone — a manual delete, a moved state
    /// directory — is forgotten so the list reflects what is actually on disk. Only registry entries are dropped;
    /// a clone folder that still exists is never removed, because it may hold uncommitted work (the same
    /// never-discard-silently discipline as the AC-85 worktree reconcile).
    /// </summary>
    Task ReconcileAsync(CancellationToken cancellationToken = default);
}
