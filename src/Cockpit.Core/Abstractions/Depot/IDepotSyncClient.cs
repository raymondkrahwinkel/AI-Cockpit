using Cockpit.Core.Depot;

namespace Cockpit.Core.Abstractions.Depot;

/// <summary>
/// Talks to one Depot connection's memory-tree content contract (AC-280): list, batch-read, batch-write, move,
/// delete and (AC-281) version-history metadata. Reuses the connection Cockpit already has to Depot
/// (<c>IMcpToolInvoker</c>, AC-243) rather than a new transport. Transport only — the shadow index, pull/push
/// cycle and merge live in later AC-278 tickets. Memory tree only, never Depot's artifacts (<c>list_artifacts</c>'
/// own surface).
/// </summary>
public interface IDepotSyncClient
{
    /// <summary>
    /// The full memory-tree listing for <paramref name="project"/>, paginating through every round Depot hands
    /// back. Fails as a whole rather than returning a partial page set if any round does not succeed. Markdown
    /// files only, per Depot's own <c>list</c> contract — a project's artifacts are not included here.
    /// </summary>
    Task<DepotListResult> ListAllAsync(
        string serverName, string project, string? path = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads <paramref name="paths"/> in as many <c>read_many</c> rounds as needed. A path Depot reports as
    /// not found lands in <see cref="DepotReadManyResult.Missing"/>; a path whose round failed outright lands in
    /// <see cref="DepotReadManyResult.Unreadable"/> — the two are never conflated.
    /// </summary>
    Task<DepotReadManyResult> ReadManyAsync(
        string serverName, string project, IReadOnlyList<string> paths, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes <paramref name="entries"/> in as many <c>write_many</c> rounds as needed. Every path gets exactly
    /// one <see cref="DepotWriteEntryResult"/> back, even one whose round failed outright.
    /// </summary>
    Task<DepotWriteManyResult> WriteManyAsync(
        string serverName, string project, IReadOnlyList<DepotWriteEntry> entries, CancellationToken cancellationToken = default);

    Task<DepotMutationResult> MoveAsync(
        string serverName, string project, string from, string to, string? baseChecksum,
        bool overwrite = false, CancellationToken cancellationToken = default);

    Task<DepotMutationResult> DeleteAsync(
        string serverName, string project, string path, string? baseChecksum,
        bool hard = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists <paramref name="path"/>'s prior overwrite versions (metadata only — no bytes). AC-281's only use for
    /// this: confirming a local shadow base still matches a checksum Depot once had for this path, once the
    /// current listing has already moved past it.
    /// </summary>
    Task<DepotListVersionsResult> ListVersionsAsync(
        string serverName, string project, string path, CancellationToken cancellationToken = default);
}
