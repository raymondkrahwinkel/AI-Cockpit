namespace Cockpit.Core.Depot;

// One file to write through `write_many` — `BaseChecksum` null only for a file that has never been written
// before; Depot's own optimistic-concurrency contract (AC-280 criterion 3).
public sealed record DepotWriteEntry(string Path, string Content, string? BaseChecksum);

public enum DepotWriteStatus
{
    Written,
    Conflict,
    Invalid,

    // Not one of Depot's own statuses — this client's own addition for a path whose round never got a per-file
    // answer at all (the round call itself failed: unreachable Depot, an over-cap batch rejected outright, or an
    // unparseable response). AC-280 criterion 4: such a path must never come back silently as Written.
    Failed,
}

public sealed record DepotWriteEntryResult(string Path, DepotWriteStatus Status, string? Checksum, string? Message);

// The whole batch's outcome (AC-280 criterion 3/4) — always one `DepotWriteEntryResult` per requested path,
// across as many rounds as the entries needed, regardless of whether any individual round succeeded.
public sealed record DepotWriteManyResult(IReadOnlyList<DepotWriteEntryResult> Results);
