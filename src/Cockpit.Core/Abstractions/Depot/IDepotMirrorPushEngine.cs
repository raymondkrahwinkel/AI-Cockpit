using Cockpit.Core.Depot;

namespace Cockpit.Core.Abstractions.Depot;

/// <summary>
/// Pushes one Depot mirror's local changes up to Depot (AC-282): the other direction from
/// <see cref="IDepotMirrorPullEngine"/>. Optimistic per file, not transactional — every write carries the
/// file's recorded baseChecksum from the shadow index, and a conflict or invalid result never overwrites
/// anything remote and never touches the local file, its base copy or its index entry. The 3-way merge that
/// resolves a conflict is AC-283; this only ever detects and reports one.
/// </summary>
public interface IDepotMirrorPushEngine
{
    Task<DepotPushResult> PushAsync(
        DepotMirror mirror, string serverName, string project, CancellationToken cancellationToken = default);
}
