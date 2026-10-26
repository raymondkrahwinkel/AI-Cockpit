using Cockpit.Plugins.Abstractions.TimeTracking;

namespace Cockpit.Plugin.Productivity.Contracts;

internal sealed record AttentionSettings(TimeSpan BlockLength, bool CountAgentWithoutYou)
{
    public static AttentionSettings Default { get; } = new(TimeSpan.FromMinutes(10), false);
}

// A stretch of clock time; Owner is the project for your time, the pane for agent time.
internal sealed record AttentionSpan(DateTimeOffset Start, DateTimeOffset End, string? Owner)
{
    public TimeSpan Duration => End - Start;
}

internal sealed record ProjectTime(string? ProjectId, TimeSpan Time);
internal sealed record MeasuredTicket(string Ticket, string? ProjectId, string? Statusline, TimeSpan Time);

// Your time (measured attention plus own entries, overlap once) and agent time beside it, never added to it
// unless CountAgentWithoutYou asks for the agents' time outside your blocks.
internal sealed record AttentionTime(
    TimeSpan Measured,
    TimeSpan Own,
    TimeSpan Overlap,
    TimeSpan AgentTime,
    IReadOnlyDictionary<string, TimeSpan> AgentWithoutYouPerSession,
    IReadOnlyList<AttentionSpan> You,
    IReadOnlyList<AttentionSpan> OwnEntries,
    IReadOnlyList<AttentionSpan> Agents,
    bool CountAgentWithoutYou)
{
    public TimeSpan YourTime => Measured + Own - Overlap;

    public TimeSpan AgentWithoutYou => _Sum(AgentWithoutYouPerSession.Values);

    public TimeSpan Total => CountAgentWithoutYou ? YourTime + AgentWithoutYou : YourTime;

    // Measured time the own entries did not claim, per project; a null key is time without a project.
    public IReadOnlyList<ProjectTime> MeasuredPerProject =>
        [.. You.GroupBy(span => span.Owner).Select(group => new ProjectTime(group.Key, _Sum(group.Select(span => span.Duration)))).OrderByDescending(project => project.Time)];

    // A day past the raw retention (AC-1719) keeps only its operator actions per project; each counts as one block.
    public static IReadOnlyList<ProjectTime> MeasuredFromDays(IEnumerable<CockpitTimelineDay> days, AttentionSettings settings) =>
        [.. days.GroupBy(day => day.ProjectId).Select(group => new ProjectTime(group.Key, settings.BlockLength * group.Sum(day => day.OperatorActions))).OrderByDescending(project => project.Time)];

    public static IReadOnlyList<MeasuredTicket> MeasuredPerTicket(IEnumerable<CockpitTimelineLine> lines, TimeSpan blockLength) =>
        [.. _ActionBlocks(lines, blockLength)
            .Where(block => string.IsNullOrWhiteSpace(block.Line.Ticket) is false)
            .GroupBy(block => block.Line.Ticket ?? string.Empty, StringComparer.Ordinal)
            .Select(group => new MeasuredTicket(
                group.Key,
                group.Select(block => block.Line.ProjectId).LastOrDefault(project => project is not null),
                group.Select(block => block.Line.Statusline).LastOrDefault(status => string.IsNullOrWhiteSpace(status) is false),
                _Sum(group.Select(block => block.Span.Duration))))
            .OrderByDescending(ticket => ticket.Time)];

    public static IReadOnlyList<ProjectTime> AgentWithoutYouPerProject(
        CockpitTimeline timeline,
        TimeSpan blockLength,
        DateTimeOffset until)
    {
        const string none = "";
        var result = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
        var measured = _Blocks(timeline.Lines, blockLength);
        foreach (var span in _Subtract(_Busy(timeline.Lines, until), measured))
        {
            var pane = span.Owner ?? none;
            var changes = timeline.Lines
                .Where(line => line.PaneId == pane && line.ProjectId is not null && line.At < span.End)
                .OrderBy(line => line.At)
                .ToList();
            var project = changes.LastOrDefault(line => line.At <= span.Start)?.ProjectId ?? none;
            var cursor = span.Start;
            foreach (var change in changes.Where(line => line.At > span.Start))
            {
                _Add(result, project, change.At - cursor);
                project = change.ProjectId ?? none;
                cursor = change.At;
            }

            _Add(result, project, span.End - cursor);
        }

        foreach (var day in timeline.Days)
        {
            _Add(result, day.ProjectId ?? none, day.AgentTime);
        }

        return [.. result.Select(pair => new ProjectTime(pair.Key.Length == 0 ? null : pair.Key, pair.Value)).OrderByDescending(project => project.Time)];
    }

    public static AttentionTime Compute(
        CockpitTimeline timeline,
        IReadOnlyList<CockpitTimeEntry> ownEntries,
        AttentionSettings settings,
        DateTimeOffset until)
    {
        var measured = _Blocks(timeline.Lines, settings.BlockLength);
        var own = _Union(ownEntries.Select(entry => new AttentionSpan(entry.StartedAt, entry.StartedAt + entry.Duration, entry.ProjectId)));
        var you = _Subtract(measured, own);
        var agents = _Busy(timeline.Lines, until);
        var withoutYou = agents
            .GroupBy(span => span.Owner ?? string.Empty)
            .ToDictionary(group => group.Key, group => _Sum(_Subtract(group, measured).Select(span => span.Duration)), StringComparer.Ordinal);
        var measuredTime = _Sum(measured.Select(span => span.Duration));
        return new AttentionTime(
            measuredTime,
            _Sum(ownEntries.Select(entry => entry.Duration)),
            measuredTime - _Sum(you.Select(span => span.Duration)),
            _Sum(agents.Select(span => span.Duration)) + _Sum(timeline.Days.Select(day => day.AgentTime)),
            withoutYou,
            you,
            [.. ownEntries.Select(entry => new AttentionSpan(entry.StartedAt, entry.StartedAt + entry.Duration, entry.ProjectId))],
            agents,
            settings.CountAgentWithoutYou);
    }

    // Each action opens or extends a block; its minutes run to the next action or the block end, to its project.
    private static List<AttentionSpan> _Blocks(IEnumerable<CockpitTimelineLine> lines, TimeSpan blockLength)
        => [.. _ActionBlocks(lines, blockLength).Select(block => block.Span)];

    private static List<(CockpitTimelineLine Line, AttentionSpan Span)> _ActionBlocks(
        IEnumerable<CockpitTimelineLine> lines,
        TimeSpan blockLength)
    {
        var actions = lines.Where(line => line.Kind == CockpitTimelineKind.Operator).OrderBy(line => line.At).ToList();
        var blocks = new List<(CockpitTimelineLine, AttentionSpan)>();
        for (var index = 0; index < actions.Count; index++)
        {
            var start = actions[index].At;
            var end = start + blockLength;
            if (index + 1 < actions.Count && actions[index + 1].At < end)
            {
                end = actions[index + 1].At;
            }

            if (end > start)
            {
                blocks.Add((actions[index], new AttentionSpan(start, end, actions[index].ProjectId)));
            }
        }

        return blocks;
    }

    private static List<AttentionSpan> _Busy(IEnumerable<CockpitTimelineLine> lines, DateTimeOffset until)
    {
        var open = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var spans = new List<AttentionSpan>();
        void Close(string pane, DateTimeOffset at)
        {
            if (open.Remove(pane, out var start) && at > start)
            {
                spans.Add(new AttentionSpan(start, at, pane));
            }
        }

        foreach (var line in lines.OrderBy(line => line.At))
        {
            if (line.Kind == CockpitTimelineKind.Stopped)
            {
                foreach (var pane in open.Keys.ToList())
                {
                    Close(pane, line.At);
                }
            }
            else if (line.PaneId is { } pane && line.Kind is CockpitTimelineKind.Status or CockpitTimelineKind.Closed)
            {
                if (line.Kind == CockpitTimelineKind.Status && line.Status is CockpitTimelineStatus.Busy or CockpitTimelineStatus.WorkingBackground)
                {
                    open.TryAdd(pane, line.At);
                }
                else
                {
                    Close(pane, line.At);
                }
            }
        }

        foreach (var pane in open.Keys.ToList())
        {
            Close(pane, until);
        }

        return spans;
    }

    private static List<AttentionSpan> _Union(IEnumerable<AttentionSpan> spans)
    {
        var merged = new List<AttentionSpan>();
        foreach (var span in spans.Where(span => span.End > span.Start).OrderBy(span => span.Start))
        {
            if (merged.Count > 0 && span.Start <= merged[^1].End)
            {
                var last = merged[^1];
                merged[^1] = last with { End = span.End > last.End ? span.End : last.End };
            }
            else
            {
                merged.Add(span);
            }
        }

        return merged;
    }

    // ponytail: O(spans × cuts), fine for a day or a week of lines; sort-and-sweep if a range grows to months.
    private static List<AttentionSpan> _Subtract(IEnumerable<AttentionSpan> spans, IReadOnlyList<AttentionSpan> cuts)
    {
        var result = new List<AttentionSpan>();
        foreach (var span in spans)
        {
            var start = span.Start;
            foreach (var cut in cuts.Where(cut => cut.End > span.Start && cut.Start < span.End).OrderBy(cut => cut.Start))
            {
                if (cut.Start > start)
                {
                    result.Add(span with { Start = start, End = cut.Start });
                }

                start = cut.End > start ? cut.End : start;
            }

            if (span.End > start)
            {
                result.Add(span with { Start = start });
            }
        }

        return result;
    }

    private static TimeSpan _Sum(IEnumerable<TimeSpan> values) => TimeSpan.FromTicks(values.Sum(value => value.Ticks));

    private static void _Add(Dictionary<string, TimeSpan> values, string key, TimeSpan value) =>
        values[key] = values.GetValueOrDefault(key) + value;
}
