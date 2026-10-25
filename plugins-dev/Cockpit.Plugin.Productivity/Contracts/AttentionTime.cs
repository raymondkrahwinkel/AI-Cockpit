using Cockpit.Plugins.Abstractions.TimeTracking;

namespace Cockpit.Plugin.Productivity.Contracts;

internal sealed record AttentionSettings(TimeSpan BlockLength, bool CountAgentWithoutYou)
{
    public static AttentionSettings Default { get; } = new(TimeSpan.FromMinutes(10), false);
}

/// <summary>A stretch of clock time; Owner is the project for your time, the pane for agent time.</summary>
internal sealed record AttentionSpan(DateTimeOffset Start, DateTimeOffset End, string? Owner)
{
    public TimeSpan Duration => End - Start;
}

/// <summary>
/// Your time (measured attention plus own entries, overlap once) and agent time beside it, never added to it
/// unless <see cref="AttentionSettings.CountAgentWithoutYou"/> asks for the agents' time outside your blocks.
/// </summary>
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
    public TimeSpan YourTime => Measured + Own;

    public TimeSpan AgentWithoutYou => _Sum(AgentWithoutYouPerSession.Values);

    public TimeSpan Total => CountAgentWithoutYou ? YourTime + AgentWithoutYou : YourTime;

    // Measured time the own entries did not claim, per project; a null key is time without a project.
    public IReadOnlyList<(string? ProjectId, TimeSpan Time)> MeasuredPerProject =>
        [.. You.GroupBy(span => span.Owner).Select(group => (group.Key, _Sum(group.Select(span => span.Duration)))).OrderByDescending(pair => pair.Item2)];

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
    {
        var actions = lines.Where(line => line.Kind == CockpitTimelineKind.Operator).OrderBy(line => line.At).ToList();
        var spans = new List<AttentionSpan>();
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
                spans.Add(new AttentionSpan(start, end, actions[index].ProjectId));
            }
        }

        return spans;
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
}
