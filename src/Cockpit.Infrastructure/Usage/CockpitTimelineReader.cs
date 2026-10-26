using Cockpit.Core.Abstractions;
using Cockpit.Core.Usage;
using Cockpit.Infrastructure.Auditing;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions.TimeTracking;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.Usage;

// AC-1720: the session timeline (AC-1717), usage history (AC-251) and day aggregates (AC-1719) of every session over a
// range, for a plugin with `timeline.read-all`. A session's day is aggregated or raw, never both: an aggregated day's raw
// leftovers (a fold that stopped before its rewrite) are dropped, and its `Closing` is the base for the next raw delta.
internal sealed class CockpitTimelineReader : ICockpitTimelineReader, ISingletonService
{
    private readonly IUsageHistory _usage;
    private readonly IReadOnlyList<SessionTimelineLog> _timeline;
    private readonly SessionDayAggregateLog _aggregates;
    private readonly TimeProvider _time;

    public CockpitTimelineReader(UsageHistoryLog usage, ILogger<SessionTimelineLog> logger)
        : this(usage, AuditTrailFiles.InStateRoot(AuditTrailFiles.SessionTimeline), AuditTrailFiles.InStateRoot(AuditTrailFiles.SessionDayAggregates), TimeProvider.System, logger)
    {
    }

    // Test seam: arbitrary files and clock.
    internal CockpitTimelineReader(IUsageHistory usage, string timelinePath, string aggregatesPath, TimeProvider time, ILogger logger)
    {
        _usage = usage;
        _timeline = [new SessionTimelineLog(timelinePath, logger), new SessionTimelineLog(UsageHistoryLog.RolloverPathFor(timelinePath), logger)];
        _aggregates = new SessionDayAggregateLog(aggregatesPath, logger);
        _time = time;
    }

    public async Task<CockpitTimeline> QueryAsync(DateTimeOffset from, DateTimeOffset to, CockpitTimelineFilter? filter = null, CancellationToken cancellationToken = default)
    {
        filter ??= new CockpitTimelineFilter();
        var entries = new List<SessionTimelineEntry>();
        IReadOnlyList<UsageSnapshot> snapshots;
        IReadOnlyList<SessionDayAggregate> aggregates;

        // Under the retention's lock, so a fold cannot move a day from the raw trails to the aggregates mid-read.
        await SessionDayRetention.TrailsLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var log in _timeline)
            {
                entries.AddRange(await log.ReadRecentAsync(int.MaxValue, cancellationToken).ConfigureAwait(false));
            }

            snapshots = await _usage.ReadRecentAsync(int.MaxValue, cancellationToken).ConfigureAwait(false);
            aggregates = await _aggregates.ReadRecentAsync(int.MaxValue, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            SessionDayRetention.TrailsLock.Release();
        }

        var zone = _time.LocalTimeZone;
        var aggregated = aggregates.Select(aggregate => (aggregate.PaneId, aggregate.Day)).ToHashSet();
        bool IsRaw(string? paneId, DateTimeOffset at) => paneId is null || !aggregated.Contains((paneId, _DayOf(at, zone)));

        // A usage record has no project; its session's timeline lines and aggregates do.
        var projects = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (paneId, projectId) in entries.Select(entry => (entry.PaneId, entry.ProjectId)).Concat(aggregates.Select(aggregate => ((string?)aggregate.PaneId, aggregate.ProjectId))))
        {
            if (paneId is not null && projectId is not null)
            {
                projects.TryAdd(paneId, projectId);
            }
        }

        var lines = entries
            .Where(entry => entry.At >= from && entry.At < to && IsRaw(entry.PaneId, entry.At) && _Matches(filter, entry.PaneId, entry.ProjectId, entry.RunId))
            .OrderBy(entry => entry.At)
            .Select(_ToLine)
            .ToList();

        var usage = new List<CockpitUsageDelta>();
        foreach (var session in snapshots.GroupBy(snapshot => snapshot.PaneId, StringComparer.Ordinal))
        {
            var ordered = session.OrderBy(snapshot => snapshot.RecordedAt).ToList();
            var firstDay = _DayOf(ordered[0].RecordedAt, zone);
            var previous = aggregates
                .Where(aggregate => aggregate.PaneId == session.Key && aggregate.Day < firstDay && aggregate.Closing is not null)
                .MaxBy(aggregate => aggregate.Day)?.Closing;
            foreach (var snapshot in ordered)
            {
                var delta = UsageSnapshots.Since(snapshot, previous);
                previous = snapshot;
                if (snapshot.RecordedAt >= from && snapshot.RecordedAt < to && IsRaw(snapshot.PaneId, snapshot.RecordedAt)
                    && _Matches(filter, snapshot.PaneId, projects.GetValueOrDefault(snapshot.PaneId), snapshot.RunId))
                {
                    usage.Add(_ToDelta(delta));
                }
            }
        }

        var days = aggregates
            .Where(aggregate => _StartOf(aggregate.Day, zone) < to && _StartOf(aggregate.Day.AddDays(1), zone) > from
                && _Matches(filter, aggregate.PaneId, aggregate.ProjectId, aggregate.RunId))
            .OrderBy(aggregate => aggregate.Day)
            .ThenBy(aggregate => aggregate.PaneId, StringComparer.Ordinal)
            .Select(_ToDay)
            .ToList();

        return new CockpitTimeline(lines, [.. usage.OrderBy(delta => delta.RecordedAt)], days);
    }

    private static DateOnly _DayOf(DateTimeOffset at, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime);

    private static DateTimeOffset _StartOf(DateOnly day, TimeZoneInfo zone)
    {
        var midnight = day.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(midnight, zone.GetUtcOffset(midnight));
    }

    private static bool _Matches(CockpitTimelineFilter filter, string? paneId, string? projectId, string? runId) =>
        (filter.PaneId is null || filter.PaneId == paneId)
        && (filter.ProjectId is null || filter.ProjectId == projectId)
        && (filter.RunId is null || filter.RunId == runId);

    private static CockpitTimelineLine _ToLine(SessionTimelineEntry entry) => new(
        Enum.Parse<CockpitTimelineKind>(entry.Kind.ToString()),
        entry.At,
        entry.PaneId,
        entry.ProjectId,
        entry.RunId,
        entry.Ticket,
        entry.Status is { } status ? Enum.Parse<CockpitTimelineStatus>(status.ToString()) : null,
        entry.Action is { } action ? Enum.Parse<CockpitOperatorAction>(action.ToString()) : null,
        entry.Statusline);

    private static CockpitUsageDelta _ToDelta(UsageSnapshot delta) => new(
        delta.PaneId,
        delta.RecordedAt,
        delta.RunId,
        delta.Model,
        delta.InputTokens,
        delta.OutputTokens,
        delta.CacheReadInputTokens,
        delta.CacheCreationInputTokens,
        delta.TotalCostUsd,
        delta.Turns)
    {
        ProfileLabel = delta.ProfileLabel,
    };

    private static CockpitTimelineDay _ToDay(SessionDayAggregate day) => new(
        day.PaneId,
        day.Day,
        day.ProjectId,
        day.RunId,
        day.Tickets,
        day.AgentTime,
        day.WaitingTime,
        day.OperatorActions,
        day.InputTokens,
        day.OutputTokens,
        day.CacheReadInputTokens,
        day.CacheCreationInputTokens,
        day.ReportedCostUsd,
        day.Turns,
        day.Model);
}
