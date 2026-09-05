using Cockpit.Core.Depot;

namespace Cockpit.Core.Abstractions.Depot;

/// <summary>
/// Mirrors a Depot project's folder onto disk under a managed area (AC-278), the same registry-plus-reconcile
/// shape as <see cref="Cockpit.Core.Abstractions.Clones.IRepositoryCloneManager"/>. This slice only owns where a
/// mirror lives on disk and that it survives a restart — pulling or pushing its content is a later ticket.
/// </summary>
public interface IDepotMirrorManager
{
    /// <summary>
    /// The mirrors root in effect now — the operator's override if set, else the managed default.
    /// </summary>
    Task<string> GetEffectiveMirrorsRootAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The full managed folder a mirror of <paramref name="slug"/> on <paramref name="instanceHost"/> would live in
    /// under <paramref name="mirrorsRoot"/> — a pure function of its inputs, deriving a filesystem-safe path segment
    /// from each even when the raw id is not one itself.
    /// </summary>
    string BuildMirrorPath(string mirrorsRoot, string instanceHost, string slug);

    /// <summary>
    /// Registers the mirror for <paramref name="instanceHost"/>/<paramref name="slug"/>, creating its folder under
    /// the effective mirrors root the first time. An existing entry is returned as-is, keeping the absolute path it
    /// already has even if the root override has since changed.
    /// </summary>
    Task<DepotMirror> EnsureAsync(string instanceHost, string slug, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DepotMirror>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reconciles the registry at startup: an entry whose folder is gone is forgotten. A mirror folder that still
    /// exists is never removed here — cleanup-policy A, the same discipline as the worktree and clone reconciles.
    /// </summary>
    Task ReconcileAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Disables or removes the mirror for <paramref name="record"/>: dropped from the registry when its folder is
    /// gone or empty, else kept and marked retained so local content already on disk is never discarded silently.
    /// Returns a notice to surface when something was left behind, else null.
    /// </summary>
    Task<string?> RemoveAsync(DepotMirror record, CancellationToken cancellationToken = default);
}
