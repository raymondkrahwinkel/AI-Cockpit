namespace Cockpit.Plugins.Abstractions.TimeTracking;

public enum CockpitTimeEntrySource
{
    Duration,
    Stopwatch,
}

public enum CockpitTimeEntryStatus
{
    Open,
    Offered,
    Booked,
}

public enum CockpitTimeEntryOutcome
{
    Booked,
    Skipped,
}

public sealed record CockpitTimeEntry(
    Guid Id,
    string Description,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    string? ProjectId,
    CockpitTimeEntrySource Source,
    CockpitTimeEntryStatus Status,
    DateTimeOffset? OfferedAt = null,
    string? OfferedToPaneId = null,
    string? OutcomeReason = null);

public sealed record CockpitRunningStopwatch(
    Guid Id,
    string Description,
    DateTimeOffset StartedAt,
    string? ProjectId);

/// <summary>
/// The operator's own time entries, stored by the host and shared with productivity plugins.
/// </summary>
public interface ICockpitTimeEntries
{
    Task<CockpitTimeEntry> AddAsync(
        string description,
        DateTimeOffset startedAt,
        TimeSpan duration,
        string? projectId,
        CockpitTimeEntrySource source,
        CancellationToken cancellationToken = default);

    Task<CockpitTimeEntry> UpdateAsync(
        Guid id,
        string description,
        DateTimeOffset startedAt,
        TimeSpan duration,
        string? projectId,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CockpitTimeEntry>> ListAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default);

    Task<CockpitRunningStopwatch> StartStopwatchAsync(
        string description,
        string? projectId,
        CancellationToken cancellationToken = default);

    Task<CockpitRunningStopwatch> UpdateStopwatchAsync(
        string description,
        string? projectId,
        CancellationToken cancellationToken = default);

    Task<CockpitTimeEntry> StopStopwatchAsync(CancellationToken cancellationToken = default);

    Task<CockpitRunningStopwatch?> GetRunningStopwatchAsync(CancellationToken cancellationToken = default);

    Task MarkOfferedAsync(
        IReadOnlyCollection<Guid> ids,
        DateTimeOffset offeredAt,
        string paneId,
        CancellationToken cancellationToken = default);

    Task SetOutcomeAsync(
        IReadOnlyCollection<Guid> ids,
        CockpitTimeEntryOutcome outcome,
        string? reason = null,
        CancellationToken cancellationToken = default);
}

internal sealed class UnavailableCockpitTimeEntries : ICockpitTimeEntries
{
    public static ICockpitTimeEntries Instance { get; } = new UnavailableCockpitTimeEntries();

    private static NotSupportedException _Unavailable() =>
        new("This Cockpit host does not provide own time entries.");

    public Task<CockpitTimeEntry> AddAsync(string description, DateTimeOffset startedAt, TimeSpan duration, string? projectId, CockpitTimeEntrySource source, CancellationToken cancellationToken = default) =>
        Task.FromException<CockpitTimeEntry>(_Unavailable());

    public Task<CockpitTimeEntry> UpdateAsync(Guid id, string description, DateTimeOffset startedAt, TimeSpan duration, string? projectId, CancellationToken cancellationToken = default) =>
        Task.FromException<CockpitTimeEntry>(_Unavailable());

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromException(_Unavailable());

    public Task<IReadOnlyList<CockpitTimeEntry>> ListAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default) =>
        Task.FromException<IReadOnlyList<CockpitTimeEntry>>(_Unavailable());

    public Task<CockpitRunningStopwatch> StartStopwatchAsync(string description, string? projectId, CancellationToken cancellationToken = default) =>
        Task.FromException<CockpitRunningStopwatch>(_Unavailable());

    public Task<CockpitRunningStopwatch> UpdateStopwatchAsync(string description, string? projectId, CancellationToken cancellationToken = default) =>
        Task.FromException<CockpitRunningStopwatch>(_Unavailable());

    public Task<CockpitTimeEntry> StopStopwatchAsync(CancellationToken cancellationToken = default) =>
        Task.FromException<CockpitTimeEntry>(_Unavailable());

    public Task<CockpitRunningStopwatch?> GetRunningStopwatchAsync(CancellationToken cancellationToken = default) =>
        Task.FromException<CockpitRunningStopwatch?>(_Unavailable());

    public Task MarkOfferedAsync(IReadOnlyCollection<Guid> ids, DateTimeOffset offeredAt, string paneId, CancellationToken cancellationToken = default) =>
        Task.FromException(_Unavailable());

    public Task SetOutcomeAsync(IReadOnlyCollection<Guid> ids, CockpitTimeEntryOutcome outcome, string? reason = null, CancellationToken cancellationToken = default) =>
        Task.FromException(_Unavailable());
}
