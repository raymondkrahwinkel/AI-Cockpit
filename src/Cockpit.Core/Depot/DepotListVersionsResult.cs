namespace Cockpit.Core.Depot;

// One entry from Depot's `list_versions` tool — a prior overwrite of a file, newest first. Metadata only:
// Depot has no non-destructive way to read the bytes behind an old version (AC-281's own founding measurement).
public sealed record DepotFileVersion(string VersionId, DateTimeOffset CreatedAt, long Size, string? Checksum);

public enum DepotListVersionsOutcome
{
    Success,
    AuthorizationRequired,
    Failed,
}

// Depot's version history for one file (AC-281 criterion 4): the only way left to confirm a local shadow
// base still matches a version Depot actually knows about, once the current listing checksum has already
// moved on. Never used to read old bytes — `restore_version` is the only tool that can, and it mutates.
public sealed record DepotListVersionsResult(DepotListVersionsOutcome Outcome, IReadOnlyList<DepotFileVersion>? Versions, string? Error)
{
    public static DepotListVersionsResult Success(IReadOnlyList<DepotFileVersion> versions) =>
        new(DepotListVersionsOutcome.Success, versions, null);

    public static DepotListVersionsResult AuthorizationRequired { get; } = new(DepotListVersionsOutcome.AuthorizationRequired, null, null);

    public static DepotListVersionsResult Failed(string error) => new(DepotListVersionsOutcome.Failed, null, error);
}
