using Cockpit.Core.Depot;

namespace Cockpit.Core.Abstractions.Depot;

public interface IDepotMirrorRegistry
{
    Task<IReadOnlyList<DepotMirror>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a mirror, replacing any earlier entry for the same instance host + slug so re-adding it cannot
    /// duplicate it.
    /// </summary>
    Task AddAsync(DepotMirror record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the entry for <paramref name="instanceHost"/>/<paramref name="slug"/>; a no-op when none matches.
    /// </summary>
    Task RemoveAsync(string instanceHost, string slug, CancellationToken cancellationToken = default);
}
