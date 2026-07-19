using Cockpit.Core.Clones;

namespace Cockpit.Core.Abstractions.Clones;

/// <summary>
/// The persistent record of every repository the cockpit cloned from a URL (AC-90), surviving a restart so an
/// already-cloned repository is reused rather than fetched afresh, and a folder that vanished is reconciled away.
/// Mirrors the AC-85 worktree registry: the record is the source of truth, read before the folders on disk.
/// </summary>
public interface IRepositoryCloneRegistry
{
    Task<IReadOnlyList<RepositoryClone>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Records a clone, replacing any earlier entry for the same path so a reuse cannot duplicate it.</summary>
    Task AddAsync(RepositoryClone record, CancellationToken cancellationToken = default);

    /// <summary>Removes the entry for <paramref name="path"/>; a no-op when none matches.</summary>
    Task RemoveAsync(string path, CancellationToken cancellationToken = default);
}
