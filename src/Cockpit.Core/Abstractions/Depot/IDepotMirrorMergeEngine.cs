using Cockpit.Core.Depot;

namespace Cockpit.Core.Abstractions.Depot;

/// <summary>
/// Resolves AC-281's diverged files with a 3-way text merge (AC-283): <c>git merge-file --diff3</c> against the
/// local base, the working copy and Depot's current content. Clean merges are written and re-based onto Depot's
/// current checksum for the next push (AC-282); conflicts get diff3 markers instead, base/index untouched.
/// Never picks a winner — an unconfirmed base or binary content is reported the same way, since no automatic
/// "newest wins" rule exists yet (AC-283 itself leaves that open).
/// </summary>
public interface IDepotMirrorMergeEngine
{
    Task<DepotMergeResult> MergeAsync(
        DepotMirror mirror, string serverName, string project, IReadOnlyList<DepotDivergedFile> diverged,
        CancellationToken cancellationToken = default);
}
