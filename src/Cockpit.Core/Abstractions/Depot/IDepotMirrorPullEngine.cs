using Cockpit.Core.Depot;

namespace Cockpit.Core.Abstractions.Depot;

/// <summary>
/// Pulls one Depot mirror's memory tree onto disk (AC-281): a shadow index plus a local base copy of every
/// mirrored file, kept under <c>.cockpit-sync/</c> in the mirror's own folder. The local base is what a later
/// 3-way merge (AC-283) diffs against — Depot can only restore an old version by overwriting the current one, so
/// there is no non-destructive way to read old bytes from Depot itself. Push and merge are later AC-278 tickets;
/// this only ever pulls, and never calls <c>restore_version</c>.
/// </summary>
public interface IDepotMirrorPullEngine
{
    Task<DepotPullResult> PullAsync(
        DepotMirror mirror, string serverName, string project, CancellationToken cancellationToken = default);
}
