namespace Cockpit.Core.Depot;

// One entry from Depot's `list` tool (AC-280) — path, size, updatedAt and, when requested, a checksum. Field
// names match Depot's own JSON casing (System.Text.Json's Web defaults lower-case the initial letter).
public sealed record DepotFileEntry(string Path, long Size, DateTimeOffset UpdatedAt, string? Checksum);

public enum DepotListOutcome
{
    Success,
    AuthorizationRequired,
    Failed,
}

// The full, paginated memory-tree listing for one Depot project (AC-280 criterion 1). A page failure part-way
// through reports the whole call Failed rather than a truncated-but-successful list — a caller diffing this
// against a local shadow index must never mistake "Depot didn't answer" for "these files are gone".
public sealed record DepotListResult(DepotListOutcome Outcome, IReadOnlyList<DepotFileEntry>? Files, string? Error)
{
    public static DepotListResult Success(IReadOnlyList<DepotFileEntry> files) => new(DepotListOutcome.Success, files, null);

    public static DepotListResult AuthorizationRequired { get; } = new(DepotListOutcome.AuthorizationRequired, null, null);

    public static DepotListResult Failed(string error) => new(DepotListOutcome.Failed, null, error);
}
