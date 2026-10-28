namespace Cockpit.Core.Configuration;

// AC-1846: a server the cockpit can keep settings in step with. Signed out: it still carries a choice but cannot be ticked.
public sealed record SyncServer(string Id, string Name, string Address, bool SignedIn);

// What the last successful run with one server left there (in memory; empty until the first run of this session).
public sealed record SyncStanding(DateTimeOffset At, long Bytes);

// Null `Categories` is an install that never chose (the sync migration has not run, or no server is connected).
public sealed record SyncOverview(
    IReadOnlyList<SyncServer> Servers,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? Categories,
    int ProfileCount,
    bool Reviewed,
    IReadOnlyList<string> SeenServers,
    IReadOnlyDictionary<string, SyncStanding> Standing);

// What Options › Sync reads and writes: which servers each category of settings is kept in step with.
public interface ISyncOptions
{
    Task<SyncOverview> ReadAsync(CancellationToken cancellationToken = default);

    // Always writes every category, as a list that may be empty: a missing `Categories` would be migrated again at the next start.
    Task SaveAsync(IReadOnlyDictionary<string, IReadOnlyList<string>> categories, IReadOnlyList<string> seenServers, CancellationToken cancellationToken = default);
}
