namespace Cockpit.Core.Depot;

// A file whose local working copy and Depot's copy both changed since the last synced base (AC-281 criterion
// 4). Push and 3-way merge are later tickets (AC-282/283); this only reports the fact and whether the recorded
// base is confirmed against Depot — it is never resolved here, and the local file is never touched for it.
public sealed record DepotDivergedFile(string Path, bool BaseConfirmed);

public enum DepotPullOutcome
{
    Success,
    AuthorizationRequired,
    Failed,
}

// The outcome of one pull of a Depot mirror (AC-281). `Pulled`/`Deleted` actually changed on disk this round;
// `Retained` are paths Depot no longer has whose working copy had itself diverged — kept rather than destroyed;
// `Unreadable` couldn't be answered for; `Diverged` need a later 3-way merge.
public sealed record DepotPullResult(
    DepotPullOutcome Outcome,
    IReadOnlyList<string> Pulled,
    IReadOnlyList<string> Deleted,
    IReadOnlyList<string> Retained,
    IReadOnlyList<string> Unreadable,
    IReadOnlyList<DepotDivergedFile> Diverged,
    string? Error)
{
    public static DepotPullResult Success(
        IReadOnlyList<string> pulled, IReadOnlyList<string> deleted, IReadOnlyList<string> retained,
        IReadOnlyList<string> unreadable, IReadOnlyList<DepotDivergedFile> diverged) =>
        new(DepotPullOutcome.Success, pulled, deleted, retained, unreadable, diverged, null);

    public static DepotPullResult AuthorizationRequired { get; } = new(DepotPullOutcome.AuthorizationRequired, [], [], [], [], [], null);

    public static DepotPullResult Failed(string error) => new(DepotPullOutcome.Failed, [], [], [], [], [], error);
}
