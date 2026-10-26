using Cockpit.Plugins.Abstractions.Projects;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.TimeTracking;

namespace Cockpit.Plugin.Productivity.Contracts;

// The overview of a range from the timeline, the own entries and the projects; shared by the channel and the
// screenshot scenes (AC-1766), so a render shows what the channel answers.
internal static class ProductivityOverview
{
    public static ProductivityOverviewSnapshot Build(
        ProductivityOverviewRequest request,
        CockpitTimeline timeline,
        IReadOnlyList<CockpitTimeEntry> entries,
        IReadOnlyList<PluginProjectInfo> projects,
        IReadOnlyList<OpenCockpitSession> open,
        WeekSettings settings,
        IReadOnlyDictionary<string, double> rates,
        DateTimeOffset now)
    {
        var until = now < request.To ? now : request.To;
        var attention = AttentionTime.Compute(timeline, entries, AttentionSettings.Default, until);
        var historicalMeasured = AttentionTime.MeasuredFromDays(timeline.Days, AttentionSettings.Default);
        var agentPerProject = AttentionTime.AgentWithoutYouPerProject(timeline, AttentionSettings.Default.BlockLength, until);
        var days = _OverviewDays(request, timeline, entries, now);
        var tickets = _OverviewTickets(timeline);
        return new ProductivityOverviewSnapshot(
            attention,
            days,
            tickets,
            historicalMeasured,
            agentPerProject,
            settings.ScheduleMinutes,
            entries,
            timeline,
            projects,
            SessionLabel.From(timeline, open),
            rates);
    }

    private static IReadOnlyList<ProductivityTicketTime> _OverviewTickets(CockpitTimeline timeline)
    {
        var result = AttentionTime.MeasuredPerTicket(timeline.Lines, AttentionSettings.Default.BlockLength)
            .Select(ticket => new ProductivityTicketTime(ticket.Ticket, ticket.ProjectId, ticket.Time, ticket.Statusline))
            .ToDictionary(ticket => ticket.Ticket, StringComparer.Ordinal);
        foreach (var day in timeline.Days)
        {
            foreach (var ticket in day.Tickets)
            {
                result.TryAdd(ticket, new ProductivityTicketTime(ticket, day.ProjectId, TimeSpan.Zero, null));
            }
        }

        return [.. result.Values.OrderByDescending(ticket => ticket.Time).ThenBy(ticket => ticket.Ticket, StringComparer.Ordinal)];
    }

    private static IReadOnlyList<ProductivityOverviewDay> _OverviewDays(
        ProductivityOverviewRequest request,
        CockpitTimeline timeline,
        IReadOnlyList<CockpitTimeEntry> entries,
        DateTimeOffset now)
    {
        var result = new List<ProductivityOverviewDay>();
        for (var day = DateOnly.FromDateTime(request.From.LocalDateTime); ; day = day.AddDays(1))
        {
            var midnight = _AtLocalTime(day);
            var from = midnight < request.From ? request.From : midnight;
            if (from >= request.To)
            {
                break;
            }

            var next = _AtLocalTime(day.AddDays(1));
            var to = next < request.To ? next : request.To;
            var dayTimeline = new CockpitTimeline(
                [.. timeline.Lines.Where(line => line.At >= from && line.At < to)],
                [.. timeline.Usage.Where(usage => usage.RecordedAt >= from && usage.RecordedAt < to)],
                [.. timeline.Days.Where(value => value.Day == day)]);
            var dayEntries = entries.Where(entry => entry.StartedAt >= from && entry.StartedAt < to).ToList();
            var historical = AttentionTime.MeasuredFromDays(dayTimeline.Days, AttentionSettings.Default);
            result.Add(new ProductivityOverviewDay(
                day,
                AttentionTime.Compute(dayTimeline, dayEntries, AttentionSettings.Default, now < to ? now : to),
                TimeSpan.FromTicks(historical.Sum(project => project.Time.Ticks))));
        }

        return result;
    }

    private static DateTimeOffset _AtLocalTime(DateOnly date)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }
}
