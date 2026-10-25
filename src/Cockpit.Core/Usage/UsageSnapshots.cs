namespace Cockpit.Core.Usage;

// Projections over the running totals in `IUsageHistory` (AC-1719, shared with AC-1453): every record carries what its
// session spent so far, so a session's spend is its latest record and a stretch's spend is a difference, never a sum.
public static class UsageSnapshots
{
    // A session's total: its record with the latest `RecordedAt`, whatever order the records come in.
    public static IReadOnlyList<UsageSnapshot> LatestPerSession(IEnumerable<UsageSnapshot> snapshots) =>
        [.. snapshots
            .GroupBy(snapshot => snapshot.PaneId, StringComparer.Ordinal)
            .Select(session => session.OrderBy(snapshot => snapshot.RecordedAt).Last())];

    // What `current` added on top of `previous`, the same session's record before it. A new runtime (another
    // `StartedAt`) starts its meter at zero, so then all of `current` is new.
    public static UsageSnapshot Since(UsageSnapshot current, UsageSnapshot? previous) =>
        previous?.StartedAt == current.StartedAt
            ? current with
            {
                InputTokens = current.InputTokens - previous.InputTokens,
                OutputTokens = current.OutputTokens - previous.OutputTokens,
                CacheReadInputTokens = current.CacheReadInputTokens - previous.CacheReadInputTokens,
                CacheCreationInputTokens = current.CacheCreationInputTokens - previous.CacheCreationInputTokens,
                TotalCostUsd = current.TotalCostUsd - previous.TotalCostUsd,
                Turns = current.Turns - previous.Turns,
            }
            : current;
}
