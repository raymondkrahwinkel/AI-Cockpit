using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Usage;
using Cockpit.Infrastructure.Auditing;
using Cockpit.Infrastructure.Sessions;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.Usage;

// AC-1719: the raw session timeline and usage history keep 90 days; older days fold into one line per session and
// local day in `session-day-aggregates.jsonl`, which stays. The own time entries (AC-1729) are a file this never opens.
// Runs at startup before the timeline recorder; idempotent, since a day already folded is never written twice.
internal sealed class SessionDayRetention : ISingletonService
{
    internal const int RawDays = 90;

    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    private readonly UsageHistoryLog _usage;
    private readonly string _timelinePath;
    private readonly SessionDayAggregateLog _aggregates;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public SessionDayRetention(UsageHistoryLog usage, ILogger<SessionDayRetention> logger)
        : this(usage, AuditTrailFiles.InStateRoot(AuditTrailFiles.SessionTimeline), AuditTrailFiles.InStateRoot(AuditTrailFiles.SessionDayAggregates), TimeProvider.System, logger)
    {
    }

    // Test seam: arbitrary files and clock.
    internal SessionDayRetention(UsageHistoryLog usage, string timelinePath, string aggregatesPath, TimeProvider time, ILogger logger)
    {
        _usage = usage;
        _timelinePath = timelinePath;
        _aggregates = new SessionDayAggregateLog(aggregatesPath, logger);
        _time = time;
        _logger = logger;
    }

    // Never throws: a retention that cannot run keeps every raw line for the next start.
    public void Run()
    {
        try
        {
            RunAsync().GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not fold the session timeline and usage history into day aggregates.");
        }
    }

    internal Task RunAsync() => _usage.RunExclusiveAsync(async () =>
    {
        var zone = _time.LocalTimeZone;
        var cut = _StartOf(_DayOf(_time.GetUtcNow(), zone).AddDays(-RawDays), zone);
        var timelinePaths = _Generations(_timelinePath);
        var usagePaths = _Generations(_usage.FilePath);
        var timeline = _Read<SessionTimelineEntry>(timelinePaths).Where(entry => entry.At < cut).ToList();
        var usage = _Read<UsageSnapshot>(usagePaths).Where(snapshot => snapshot.RecordedAt < cut).ToList();
        if (timeline.Count == 0 && usage.Count == 0)
        {
            return;
        }

        var existing = await _aggregates.ReadRecentAsync(int.MaxValue).ConfigureAwait(false);
        var days = new Dictionary<(string PaneId, DateOnly Day), Day>();
        var carried = _FoldTimeline(timeline, cut, zone, days);
        _FoldUsage(usage, existing, zone, days);

        var written = existing.Select(aggregate => (aggregate.PaneId, aggregate.Day)).ToHashSet();
        foreach (var (key, day) in days.OrderBy(pair => pair.Key.Day).ThenBy(pair => pair.Key.PaneId, StringComparer.Ordinal))
        {
            if (written.Add(key))
            {
                await _aggregates.AppendAsync(day.ToAggregate(key.PaneId, key.Day)).ConfigureAwait(false);
            }
        }

        // Only once every day is safely folded do the raw lines go.
        foreach (var path in usagePaths)
        {
            await _KeepFromAsync<UsageSnapshot>(path, snapshot => snapshot.RecordedAt >= cut, []).ConfigureAwait(false);
        }

        var oldest = timelinePaths.FirstOrDefault(File.Exists);
        foreach (var path in timelinePaths)
        {
            await _KeepFromAsync<SessionTimelineEntry>(path, entry => entry.At >= cut, path == oldest ? carried : []).ConfigureAwait(false);
        }
    });

    // Busy and background count as agent time, NeedsAttention as waiting on the operator. An interval still open at the
    // cut is cut there, and returned as a status line at the cut so the raw lines after it still know it is open.
    private static List<SessionTimelineEntry> _FoldTimeline(
        List<SessionTimelineEntry> lines,
        DateTimeOffset cut,
        TimeZoneInfo zone,
        Dictionary<(string, DateOnly), Day> days)
    {
        var open = new Dictionary<string, SessionTimelineEntry>(StringComparer.Ordinal);
        var last = new Dictionary<string, SessionTimelineEntry>(StringComparer.Ordinal);

        void Close(string paneId, DateTimeOffset end)
        {
            if (open.Remove(paneId, out var start))
            {
                _AddInterval(paneId, start, end, zone, days);
            }
        }

        foreach (var line in lines.OrderBy(line => line.At))
        {
            if (line.PaneId is not { } paneId)
            {
                foreach (var pane in open.Keys.ToList())
                {
                    Close(pane, line.At);
                }

                continue;
            }

            last[paneId] = line;
            var day = _At(days, paneId, _DayOf(line.At, zone));
            day.Note(line.ProjectId, line.RunId, line.Ticket);
            switch (line.Kind)
            {
                case SessionTimelineKind.Operator:
                    day.OperatorActions++;
                    break;
                case SessionTimelineKind.Closed:
                    Close(paneId, line.At);
                    break;
                case SessionTimelineKind.Status:
                    Close(paneId, line.At);
                    if (line.Status is SessionStatus.Busy or SessionStatus.WorkingBackground or SessionStatus.NeedsAttention)
                    {
                        open[paneId] = line;
                    }

                    break;
            }
        }

        var carried = new List<SessionTimelineEntry>();
        foreach (var (paneId, start) in open)
        {
            _AddInterval(paneId, start, cut, zone, days);
            carried.Add(last[paneId] with { Kind = SessionTimelineKind.Status, At = cut, Status = start.Status, Action = null, Statusline = null });
        }

        return carried;
    }

    private static void _AddInterval(string paneId, SessionTimelineEntry start, DateTimeOffset end, TimeZoneInfo zone, Dictionary<(string, DateOnly), Day> days)
    {
        var from = start.At;
        while (from < end)
        {
            var dayOf = _DayOf(from, zone);
            var until = _StartOf(dayOf.AddDays(1), zone);
            var to = until < end ? until : end;
            var day = _At(days, paneId, dayOf);
            day.Note(start.ProjectId, start.RunId, start.Ticket);
            if (start.Status == SessionStatus.NeedsAttention)
            {
                day.WaitingTime += to - from;
            }
            else
            {
                day.AgentTime += to - from;
            }

            from = to;
        }
    }

    // Each record's difference from the one before it, summed per day. The record before the first raw one is the last
    // a folded day closed on, so a session that spans the cut is not counted from zero again.
    private static void _FoldUsage(
        List<UsageSnapshot> snapshots,
        IReadOnlyList<SessionDayAggregate> existing,
        TimeZoneInfo zone,
        Dictionary<(string, DateOnly), Day> days)
    {
        foreach (var session in snapshots.GroupBy(snapshot => snapshot.PaneId, StringComparer.Ordinal))
        {
            var ordered = session.OrderBy(snapshot => snapshot.RecordedAt).ToList();
            var firstDay = _DayOf(ordered[0].RecordedAt, zone);
            var previous = existing
                .Where(aggregate => aggregate.PaneId == session.Key && aggregate.Day < firstDay && aggregate.Closing is not null)
                .OrderBy(aggregate => aggregate.Day)
                .LastOrDefault()?.Closing;
            foreach (var snapshot in ordered)
            {
                var day = _At(days, session.Key, _DayOf(snapshot.RecordedAt, zone));
                day.Add(UsageSnapshots.Since(snapshot, previous));
                day.Closing = snapshot;
                day.Note(null, snapshot.RunId, null);
                previous = snapshot;
            }
        }
    }

    private static Day _At(Dictionary<(string, DateOnly), Day> days, string paneId, DateOnly day)
    {
        if (days.TryGetValue((paneId, day), out var found))
        {
            return found;
        }

        var created = new Day();
        days[(paneId, day)] = created;
        return created;
    }

    private static DateOnly _DayOf(DateTimeOffset at, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime);

    private static DateTimeOffset _StartOf(DateOnly day, TimeZoneInfo zone)
    {
        var midnight = day.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(midnight, zone.GetUtcOffset(midnight));
    }

    // The rollover generation first: it holds the older lines.
    private static List<string> _Generations(string path) => [UsageHistoryLog.RolloverPathFor(path), path];

    private static IEnumerable<T> _Read<T>(IEnumerable<string> paths)
        where T : class =>
        paths.Where(File.Exists).SelectMany(File.ReadLines).Select(_Parse<T>).OfType<T>().ToList();

    private static T? _Parse<T>(string line)
        where T : class
    {
        try
        {
            return string.IsNullOrWhiteSpace(line) ? null : JsonSerializer.Deserialize<T>(line, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Rewrites `path` with `prepended` and every line that is kept, or that does not parse: a line this cannot read is
    // not a line it may drop. Written aside and moved over, so a crash leaves the old file or the new one.
    private static async Task _KeepFromAsync<T>(string path, Func<T, bool> keep, IReadOnlyList<SessionTimelineEntry> prepended)
        where T : class
    {
        if (!File.Exists(path))
        {
            return;
        }

        var lines = await File.ReadAllLinesAsync(path).ConfigureAwait(false);
        var kept = lines.Where(line => _Parse<T>(line) is not { } entry || keep(entry)).ToList();
        if (kept.Count == lines.Length && prepended.Count == 0)
        {
            return;
        }

        var text = new StringBuilder();
        foreach (var line in prepended.Select(entry => JsonSerializer.Serialize(entry, Json)).Concat(kept))
        {
            text.Append(line).Append(Environment.NewLine);
        }

        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        var temporary = path + ".retention";
        await using (var stream = new FileStream(temporary, options))
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes(text.ToString())).ConfigureAwait(false);
        }

        File.Move(temporary, path, overwrite: true);
    }

    private sealed class Day
    {
        private readonly SortedSet<string> _tickets = new(StringComparer.Ordinal);
        private string? _projectId;
        private string? _runId;
        private UsageSnapshot? _spent;

        public TimeSpan AgentTime { get; set; }

        public TimeSpan WaitingTime { get; set; }

        public int OperatorActions { get; set; }

        public UsageSnapshot? Closing { get; set; }

        public void Note(string? projectId, string? runId, string? ticket)
        {
            _projectId = projectId ?? _projectId;
            _runId = runId ?? _runId;
            if (ticket is not null)
            {
                _tickets.Add(ticket);
            }
        }

        public void Add(UsageSnapshot spent) => _spent = _spent is null
            ? spent
            : spent with
            {
                InputTokens = _spent.InputTokens + spent.InputTokens,
                OutputTokens = _spent.OutputTokens + spent.OutputTokens,
                CacheReadInputTokens = _spent.CacheReadInputTokens + spent.CacheReadInputTokens,
                CacheCreationInputTokens = _spent.CacheCreationInputTokens + spent.CacheCreationInputTokens,
                TotalCostUsd = _spent.TotalCostUsd + spent.TotalCostUsd,
                Turns = _spent.Turns + spent.Turns,
            };

        public SessionDayAggregate ToAggregate(string paneId, DateOnly day) => new(
            paneId,
            day,
            _projectId,
            _runId,
            [.. _tickets],
            AgentTime,
            WaitingTime,
            OperatorActions,
            _spent?.InputTokens ?? 0,
            _spent?.OutputTokens ?? 0,
            _spent?.CacheReadInputTokens ?? 0,
            _spent?.CacheCreationInputTokens ?? 0,
            _spent?.TotalCostUsd ?? 0,
            _spent?.Turns ?? 0,
            _spent?.Model,
            Closing);
    }
}

// One session's local day once its raw lines are gone. Tokens, cost and turns are that day's differences; the reported
// cost only, since an estimate is the tokens times `Model`'s price (AC-1687). `Closing` is the running total the day
// ended on: subtract it from the session's first raw record after the cut (AC-1720) instead of counting that from zero.
internal sealed record SessionDayAggregate(
    string PaneId,
    DateOnly Day,
    string? ProjectId,
    string? RunId,
    IReadOnlyList<string> Tickets,
    TimeSpan AgentTime,
    TimeSpan WaitingTime,
    int OperatorActions,
    int InputTokens,
    int OutputTokens,
    int CacheReadInputTokens,
    int CacheCreationInputTokens,
    double ReportedCostUsd,
    int Turns,
    string? Model,
    UsageSnapshot? Closing);

internal sealed class SessionDayAggregateLog : JsonlAuditLog<SessionDayAggregate>
{
    public SessionDayAggregateLog(ILogger<SessionDayAggregateLog> logger)
        : this(AuditTrailFiles.InStateRoot(AuditTrailFiles.SessionDayAggregates), logger)
    {
    }

    // Test seam: an arbitrary file.
    internal SessionDayAggregateLog(string path, ILogger logger)
        : base(path, logger)
    {
    }

    protected override string LogName => "session day aggregates";

    protected override SessionDayAggregate PrepareForWrite(SessionDayAggregate entry) => entry;

    // Strict: the raw lines are deleted after this, so a failed write must stop that.
    public Task AppendAsync(SessionDayAggregate entry) => RecordStrictAsync(entry);
}
