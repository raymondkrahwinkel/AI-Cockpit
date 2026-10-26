using Cockpit.Core.Abstractions;
using Cockpit.Infrastructure.Auditing;
using Cockpit.Plugins.Abstractions.TimeTracking;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.Usage;

internal sealed class CockpitTimeEntriesLog : JsonlAuditLog<CockpitTimeEntryEvent>, ICockpitTimeEntries, ISingletonService
{
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly TimeProvider _time;

    public CockpitTimeEntriesLog(ILogger<CockpitTimeEntriesLog> logger)
        : this(AuditTrailFiles.InStateRoot(AuditTrailFiles.OwnTimeEntries), logger, TimeProvider.System)
    {
    }

    internal CockpitTimeEntriesLog(string path, ILogger<CockpitTimeEntriesLog> logger, TimeProvider time)
        : base(path, logger)
    {
        _time = time;
    }

    protected override string LogName => "own time entries";

    protected override CockpitTimeEntryEvent PrepareForWrite(CockpitTimeEntryEvent entry) => entry;

    public async Task<CockpitTimeEntry> AddAsync(
        string description,
        DateTimeOffset startedAt,
        TimeSpan duration,
        string? projectId,
        CockpitTimeEntrySource source,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(source))
        {
            throw new ArgumentOutOfRangeException(nameof(source));
        }

        var entry = new CockpitTimeEntry(
            Guid.NewGuid(),
            _Description(description),
            startedAt,
            _Duration(duration),
            _ProjectId(projectId),
            source,
            CockpitTimeEntryStatus.Open);

        await _RecordAsync(new(CockpitTimeEntryEventKind.EntrySaved, _time.GetUtcNow(), Entry: entry), cancellationToken).ConfigureAwait(false);
        return entry;
    }

    public async Task<CockpitTimeEntry> UpdateAsync(
        Guid id,
        string description,
        DateTimeOffset startedAt,
        TimeSpan duration,
        string? projectId,
        CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await _ReadStateAsync(cancellationToken).ConfigureAwait(false);
            var current = _EditableEntry(state, id);
            var updated = current with
            {
                Description = _Description(description),
                StartedAt = startedAt,
                Duration = _Duration(duration),
                ProjectId = _ProjectId(projectId),
            };
            await RecordStrictAsync(new(CockpitTimeEntryEventKind.EntrySaved, _time.GetUtcNow(), Entry: updated), cancellationToken).ConfigureAwait(false);
            return updated;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await _ReadStateAsync(cancellationToken).ConfigureAwait(false);
            _EditableEntry(state, id);
            await RecordStrictAsync(new(CockpitTimeEntryEventKind.EntryDeleted, _time.GetUtcNow(), EntryIds: [id]), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<IReadOnlyList<CockpitTimeEntry>> ListAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default)
    {
        if (from >= to)
        {
            throw new ArgumentException("The start of the range must be before its end.", nameof(from));
        }

        var state = await _ReadStateAsync(cancellationToken).ConfigureAwait(false);
        return [.. state.Entries.Values
            .Where(entry => entry.StartedAt >= from && entry.StartedAt < to)
            .OrderBy(entry => entry.StartedAt)];
    }

    public async Task<CockpitRunningStopwatch> StartStopwatchAsync(
        string description,
        string? projectId,
        CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await _ReadStateAsync(cancellationToken).ConfigureAwait(false);
            if (state.Stopwatch is not null)
            {
                throw new InvalidOperationException("A stopwatch is already running. Stop it before starting another one.");
            }

            var stopwatch = new CockpitRunningStopwatch(
                Guid.NewGuid(),
                _Description(description),
                _time.GetUtcNow(),
                _ProjectId(projectId));
            await RecordStrictAsync(new(CockpitTimeEntryEventKind.StopwatchStarted, _time.GetUtcNow(), Stopwatch: stopwatch), cancellationToken).ConfigureAwait(false);
            return stopwatch;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<CockpitRunningStopwatch> UpdateStopwatchAsync(
        string description,
        string? projectId,
        CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await _ReadStateAsync(cancellationToken).ConfigureAwait(false);
            var stopwatch = state.Stopwatch ?? throw new InvalidOperationException("No stopwatch is running.");
            var updated = stopwatch with
            {
                Description = _Description(description),
                ProjectId = _ProjectId(projectId),
            };
            await RecordStrictAsync(new(CockpitTimeEntryEventKind.StopwatchStarted, _time.GetUtcNow(), Stopwatch: updated), cancellationToken).ConfigureAwait(false);
            return updated;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<CockpitTimeEntry> StopStopwatchAsync(CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await _ReadStateAsync(cancellationToken).ConfigureAwait(false);
            var stopwatch = state.Stopwatch ?? throw new InvalidOperationException("No stopwatch is running.");
            var now = _time.GetUtcNow();
            var duration = now > stopwatch.StartedAt ? now - stopwatch.StartedAt : TimeSpan.Zero;
            var entry = new CockpitTimeEntry(
                stopwatch.Id,
                stopwatch.Description,
                stopwatch.StartedAt,
                duration,
                stopwatch.ProjectId,
                CockpitTimeEntrySource.Stopwatch,
                CockpitTimeEntryStatus.Open);
            await RecordStrictAsync(new(CockpitTimeEntryEventKind.StopwatchStopped, now, Entry: entry), cancellationToken).ConfigureAwait(false);
            return entry;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<CockpitRunningStopwatch?> GetRunningStopwatchAsync(CancellationToken cancellationToken = default) =>
        (await _ReadStateAsync(cancellationToken).ConfigureAwait(false)).Stopwatch;

    public async Task MarkOfferedAsync(
        IReadOnlyCollection<Guid> ids,
        DateTimeOffset offeredAt,
        string paneId,
        CancellationToken cancellationToken = default)
    {
        var entryIds = _EntryIds(ids);
        var targetPaneId = string.IsNullOrWhiteSpace(paneId)
            ? throw new ArgumentException("The pane id is required.", nameof(paneId))
            : paneId.Trim();

        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await _ReadStateAsync(cancellationToken).ConfigureAwait(false);
            foreach (var id in entryIds)
            {
                _EditableEntry(state, id);
            }

            await RecordStrictAsync(new(
                CockpitTimeEntryEventKind.EntriesOffered,
                _time.GetUtcNow(),
                EntryIds: entryIds,
                OfferedAt: offeredAt,
                OfferedToPaneId: targetPaneId), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task SetOutcomeAsync(
        IReadOnlyCollection<Guid> ids,
        CockpitTimeEntryOutcome outcome,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        var entryIds = _EntryIds(ids);
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await _ReadStateAsync(cancellationToken).ConfigureAwait(false);
            foreach (var id in entryIds)
            {
                if (!state.Entries.TryGetValue(id, out var entry))
                {
                    throw new KeyNotFoundException($"Time entry '{id}' does not exist.");
                }

                if (entry.Status != CockpitTimeEntryStatus.Offered)
                {
                    throw new InvalidOperationException($"Time entry '{id}' has not been offered.");
                }
            }

            await RecordStrictAsync(new(
                CockpitTimeEntryEventKind.OutcomeSet,
                _time.GetUtcNow(),
                EntryIds: entryIds,
                Outcome: outcome,
                Reason: string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task _RecordAsync(CockpitTimeEntryEvent entry, CancellationToken cancellationToken)
    {
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RecordStrictAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task<CockpitTimeEntriesState> _ReadStateAsync(CancellationToken cancellationToken)
    {
        var events = await ReadRecentAsync(int.MaxValue, cancellationToken).ConfigureAwait(false);
        var state = new CockpitTimeEntriesState();
        foreach (var entryEvent in events.Reverse())
        {
            _Apply(state, entryEvent);
        }

        return state;
    }

    private static void _Apply(CockpitTimeEntriesState state, CockpitTimeEntryEvent entryEvent)
    {
        switch (entryEvent.Kind)
        {
            case CockpitTimeEntryEventKind.EntrySaved when entryEvent.Entry is { } entry:
                state.Entries[entry.Id] = entry;
                break;
            case CockpitTimeEntryEventKind.EntryDeleted:
                foreach (var id in entryEvent.EntryIds ?? [])
                {
                    state.Entries.Remove(id);
                }
                break;
            case CockpitTimeEntryEventKind.StopwatchStarted when entryEvent.Stopwatch is { } stopwatch:
                state.Stopwatch = stopwatch;
                break;
            case CockpitTimeEntryEventKind.StopwatchStopped when entryEvent.Entry is { } stopped:
                state.Stopwatch = null;
                state.Entries[stopped.Id] = stopped;
                break;
            case CockpitTimeEntryEventKind.EntriesOffered:
                foreach (var id in entryEvent.EntryIds ?? [])
                {
                    if (state.Entries.TryGetValue(id, out var offered))
                    {
                        state.Entries[id] = offered with
                        {
                            Status = CockpitTimeEntryStatus.Offered,
                            OfferedAt = entryEvent.OfferedAt,
                            OfferedToPaneId = entryEvent.OfferedToPaneId,
                            OutcomeReason = null,
                        };
                    }
                }
                break;
            case CockpitTimeEntryEventKind.OutcomeSet:
                foreach (var id in entryEvent.EntryIds ?? [])
                {
                    if (state.Entries.TryGetValue(id, out var resolved))
                    {
                        if (entryEvent.Outcome == CockpitTimeEntryOutcome.Booked)
                        {
                            state.Entries[id] = resolved with
                            {
                                Status = CockpitTimeEntryStatus.Booked,
                                OutcomeReason = entryEvent.Reason,
                            };
                        }
                        else if (entryEvent.Outcome == CockpitTimeEntryOutcome.Skipped)
                        {
                            state.Entries[id] = resolved with
                            {
                                Status = CockpitTimeEntryStatus.Open,
                                OfferedAt = null,
                                OfferedToPaneId = null,
                                OutcomeReason = entryEvent.Reason,
                            };
                        }
                    }
                }
                break;
        }
    }

    private static CockpitTimeEntry _EditableEntry(CockpitTimeEntriesState state, Guid id)
    {
        if (!state.Entries.TryGetValue(id, out var entry))
        {
            throw new KeyNotFoundException($"Time entry '{id}' does not exist.");
        }

        if (entry.Status != CockpitTimeEntryStatus.Open)
        {
            throw new InvalidOperationException($"Time entry '{id}' is {entry.Status.ToString().ToLowerInvariant()} and cannot be changed.");
        }

        return entry;
    }

    private static string _Description(string description) =>
        string.IsNullOrWhiteSpace(description)
            ? throw new ArgumentException("A description is required.", nameof(description))
            : description.Trim();

    private static TimeSpan _Duration(TimeSpan duration) =>
        duration <= TimeSpan.Zero
            ? throw new ArgumentOutOfRangeException(nameof(duration), "The duration must be greater than zero.")
            : duration;

    private static string? _ProjectId(string? projectId) =>
        string.IsNullOrWhiteSpace(projectId) ? null : projectId.Trim();

    private static Guid[] _EntryIds(IReadOnlyCollection<Guid> ids)
    {
        var entryIds = ids.Distinct().ToArray();
        return entryIds.Length == 0
            ? throw new ArgumentException("At least one time entry is required.", nameof(ids))
            : entryIds;
    }

    private sealed class CockpitTimeEntriesState
    {
        public Dictionary<Guid, CockpitTimeEntry> Entries { get; } = [];

        public CockpitRunningStopwatch? Stopwatch { get; set; }
    }
}

internal enum CockpitTimeEntryEventKind
{
    EntrySaved,
    EntryDeleted,
    StopwatchStarted,
    StopwatchStopped,
    EntriesOffered,
    OutcomeSet,
}

internal sealed record CockpitTimeEntryEvent(
    CockpitTimeEntryEventKind Kind,
    DateTimeOffset OccurredAt,
    CockpitTimeEntry? Entry = null,
    CockpitRunningStopwatch? Stopwatch = null,
    IReadOnlyList<Guid>? EntryIds = null,
    DateTimeOffset? OfferedAt = null,
    string? OfferedToPaneId = null,
    CockpitTimeEntryOutcome? Outcome = null,
    string? Reason = null);
