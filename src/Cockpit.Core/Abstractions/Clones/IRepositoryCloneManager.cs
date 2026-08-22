using Cockpit.Core.Clones;

namespace Cockpit.Core.Abstractions.Clones;

/// <summary>
/// Clones a git repository from a URL into a managed area and hands back the local path a session starts in (AC-90),
/// so an agent can be put on a repository not yet on this machine. Composes with worktree isolation (AC-85): the
/// clone is the repository root, each session worktrees off it. Host-first, not per-provider. Authentication is left
/// to the host's own git credential helper (GCM, <c>gh</c>) with terminal prompting disabled, so a missing helper
/// fails with a message rather than hanging, and a token is never put in the URL, where it would land in
/// <c>.git/config</c>, the process arguments and the logs.
/// </summary>
public interface IRepositoryCloneManager
{
    /// <summary>
    /// Clones <paramref name="url"/> into <paramref name="targetPath"/> — or, when null/blank, the managed
    /// <c>host/org/repo</c> folder under the clones root (<see cref="BuildClonePath"/>) — and returns its record,
    /// reusing (fetching up to date) an existing checkout of the same repository rather than re-cloning it. Throws
    /// with what git said when the clone fails, or when the target folder already holds a <em>different</em>
    /// repository, which is never silently clobbered.
    /// </summary>
    Task<RepositoryClone> CloneAsync(string url, string? targetPath = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// The clones root in effect now — the operator's override (AC-90) if set, else the managed default. Resolved
    /// once when the clone dialog opens, so its target preview reflects where clones land without re-reading the
    /// setting on every keystroke.
    /// </summary>
    Task<string> GetEffectiveClonesRootAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The full managed folder <paramref name="url"/> would clone into under <paramref name="clonesRoot"/> —
    /// <c>clonesRoot/host/org/repo</c> — or null when the URL cannot be parsed. A pure function of its inputs, so
    /// the dialog can pre-fill and live-update an editable target, sharing the one slug-parsing rule with the clone.
    /// </summary>
    string? BuildClonePath(string clonesRoot, string url);

    Task<IReadOnlyList<RepositoryClone>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reconciles the registry at startup (AC-90): a record whose folder is gone is forgotten so the list reflects
    /// disk. A clone folder that still exists is never removed — it may hold uncommitted work (the same
    /// never-discard-silently discipline as the AC-85 worktree reconcile).
    /// </summary>
    Task ReconcileAsync(CancellationToken cancellationToken = default);
}
