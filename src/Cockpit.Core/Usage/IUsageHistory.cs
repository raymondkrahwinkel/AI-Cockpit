namespace Cockpit.Core.Usage;

/// <summary>
/// Keeps what sessions spend (AC-251), so "what did yesterday's Autopilot run cost" has an answer after the app
/// has been closed. Until this existed the figure lived only in the header's meter, which dies with its session —
/// which is why the reduction work this measures could not say what it started from.
/// </summary>
public interface IUsageHistory
{
    /// <summary>
    /// Appends a snapshot. Never throws: losing a measurement must not take a session's turn down with it.
    /// </summary>
    Task RecordAsync(UsageSnapshot snapshot, CancellationToken cancellationToken = default);

    /// <summary>
    /// The most recent snapshots, newest first. Several per session: a session's total is its record with the
    /// latest <see cref="UsageSnapshot.RecordedAt"/>. Read that from the record rather than from the order, which
    /// is the order the lines were appended in and can differ when two sessions write at the same moment.
    /// </summary>
    Task<IReadOnlyList<UsageSnapshot>> ReadRecentAsync(int limit = 200, CancellationToken cancellationToken = default);
}
