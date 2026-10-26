using System.Text.Json;
using Cockpit.Plugin.Productivity.Contracts;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.TimeTracking;

namespace Cockpit.Plugin.Productivity;

internal sealed class ProductivityChannelHandlers(ICockpitHost host, ICockpitTimeEntries timeEntries, SemaphoreSlim reportGate)
{
    private readonly WeekOfferSender _offers = new(host.Actions, host.Sessions, timeEntries, host.Storage, TimeProvider.System, reportGate);

    public IReadOnlyList<IDisposable> Register() =>
    [
        _Handle<ProductivitySnapshotRequest, ProductivitySnapshot>(ProductivityChannel.Snapshot, _SnapshotAsync),
        _Handle<ProductivityOverviewRequest, ProductivityOverviewSnapshot>(ProductivityChannel.Overview, _OverviewAsync),
        _Handle<ProductivitySaveRequest, CockpitTimeEntry>(ProductivityChannel.Add, request =>
            timeEntries.AddAsync(request.Description, request.StartedAt, request.Duration, request.ProjectId, CockpitTimeEntrySource.Duration)),
        _Handle<ProductivitySaveRequest, CockpitTimeEntry>(ProductivityChannel.Update, request =>
            timeEntries.UpdateAsync(request.Id ?? throw new InvalidOperationException("The time entry id is missing."), request.Description, request.StartedAt, request.Duration, request.ProjectId)),
        _Handle<ProductivityDeleteRequest, ProductivityAcknowledgement>(ProductivityChannel.Delete, async request =>
        {
            await timeEntries.DeleteAsync(request.Id).ConfigureAwait(false);
            return new ProductivityAcknowledgement(true);
        }),
        _Handle<ProductivityStopwatchRequest, CockpitRunningStopwatch>(ProductivityChannel.StartStopwatch, request =>
            timeEntries.StartStopwatchAsync(request.Description, request.ProjectId)),
        _Handle<ProductivityStopwatchRequest, CockpitRunningStopwatch>(ProductivityChannel.UpdateStopwatch, request =>
            timeEntries.UpdateStopwatchAsync(request.Description, request.ProjectId)),
        _Handle<object, CockpitTimeEntry>(ProductivityChannel.StopStopwatch, _ => timeEntries.StopStopwatchAsync()),
        _Handle<ProductivityWeekRequest, WeekProposal>(ProductivityChannel.Week, _WeekAsync),
        _Handle<WeekOfferOptionsRequest, WeekOfferOptions>(ProductivityChannel.WeekOfferOptions, _OfferOptionsAsync),
        _Handle<WeekOfferSendRequest, WeekOfferSendResult>(ProductivityChannel.SendWeekOffer, _offers.SendAsync),
    ];

    private async Task<ProductivityOverviewSnapshot> _OverviewAsync(ProductivityOverviewRequest request)
    {
        var timeline = await host.Timeline.QueryAsync(request.From, request.To).ConfigureAwait(false);
        var entries = await timeEntries.ListAsync(request.From, request.To).ConfigureAwait(false);
        var projects = await host.GetProjectsAsync().ConfigureAwait(false);
        var settings = host.Storage.Get<WeekSettings>(WeekSettings.StorageKey) ?? WeekSettings.Default;
        var now = DateTimeOffset.Now;
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
            projects);
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

    private async Task<ProductivitySnapshot> _SnapshotAsync(ProductivitySnapshotRequest request)
    {
        var entries = await timeEntries.ListAsync(request.From, request.To).ConfigureAwait(false);
        var todayEntries = request.From == request.TodayFrom && request.To == request.TodayTo
            ? entries
            : await timeEntries.ListAsync(request.TodayFrom, request.TodayTo).ConfigureAwait(false);
        var stopwatch = await timeEntries.GetRunningStopwatchAsync().ConfigureAwait(false);
        var day = await _AttentionAsync(request.From, request.To, entries).ConfigureAwait(false);
        var today = ReferenceEquals(todayEntries, entries)
            ? day
            : await _AttentionAsync(request.TodayFrom, request.TodayTo, todayEntries).ConfigureAwait(false);
        return new ProductivitySnapshot(entries, today.YourTime, stopwatch, day);
    }

    private async Task<WeekProposal> _WeekAsync(ProductivityWeekRequest request)
    {
        var settings = host.Storage.Get<WeekSettings>(WeekSettings.StorageKey) ?? WeekSettings.Default;
        var names = (await host.GetProjectsAsync().ConfigureAwait(false)).ToDictionary(project => project.Id, project => project.Name, StringComparer.Ordinal);
        var days = new List<WeekDayInput>();
        for (var index = 0; index + 1 < request.DayStarts.Count; index++)
        {
            var (from, to) = (request.DayStarts[index], request.DayStarts[index + 1]);
            var entries = await timeEntries.ListAsync(from, to).ConfigureAwait(false);
            var timeline = await host.Timeline.QueryAsync(from, to).ConfigureAwait(false);
            var now = DateTimeOffset.Now;
            var attention = AttentionTime.Compute(timeline, entries, AttentionSettings.Default, now < to ? now : to);
            var tickets = timeline.Lines
                .Where(line => line.ProjectId is not null && line.Ticket is not null)
                .Select(line => (Project: line.ProjectId ?? string.Empty, Ticket: line.Ticket ?? string.Empty))
                .Concat(timeline.Days.Where(day => day.ProjectId is not null).SelectMany(day => day.Tickets.Select(ticket => (Project: day.ProjectId ?? string.Empty, Ticket: ticket))))
                .GroupBy(pair => pair.Project, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Select(pair => pair.Ticket).Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
            days.Add(new WeekDayInput(
                DateOnly.FromDateTime(from.LocalDateTime),
                entries,
                [.. attention.MeasuredPerProject, .. AttentionTime.MeasuredFromDays(timeline.Days, AttentionSettings.Default)],
                tickets));
        }

        var weekStart = DateOnly.FromDateTime(request.DayStarts[0].LocalDateTime);
        var overrides = host.Storage.Get<CellOverride[]>(CellOverride.StorageKey(weekStart)) ?? [];
        var frozen = (host.Storage.Get<FrozenProjectCell[]>(FrozenProjectCell.StorageKey(weekStart)) ?? [])
            .Select(cell => cell.ToProposalCell())
            .ToList();
        return WeekProposal.Build(days, settings, frozen, names, overrides) with
        {
            Report = host.Storage.Get<WeekReport>(WeekReport.StorageKey(weekStart)) ?? WeekReport.Empty,
        };
    }

    private async Task<WeekOfferOptions> _OfferOptionsAsync(WeekOfferOptionsRequest request)
    {
        var sessions = host.Sessions.OpenSessions
            .Where(session => string.Equals(session.PaneId, "cockpit-assistant", StringComparison.Ordinal) == false)
            .Select(session => new WeekOfferChoice(
                WeekOfferDestination.Session(session.PaneId, session.Name),
                session.ProfileLabel,
                session.Status))
            .ToList();
        var profiles = (await host.GetProfilesAsync().ConfigureAwait(false))
            .Select(profile => new WeekOfferChoice(WeekOfferDestination.Profile(profile.Label)))
            .ToList();
        var remembered = host.Storage.Get<WeekOfferDestination>(WeekOfferDestination.StorageKey(request.ProjectId));
        if (remembered is not null && sessions.Concat(profiles).All(choice => choice.Destination.Kind != remembered.Kind || string.Equals(choice.Destination.Id, remembered.Id, StringComparison.Ordinal) == false))
        {
            remembered = null;
        }

        return new WeekOfferOptions(sessions, profiles, remembered);
    }

    // ponytail: a session busy across the range start counts from its first line in range; query earlier if agent time matters there.
    private async Task<AttentionTime> _AttentionAsync(DateTimeOffset from, DateTimeOffset to, IReadOnlyList<CockpitTimeEntry> entries)
    {
        var timeline = await host.Timeline.QueryAsync(from, to).ConfigureAwait(false);
        var now = DateTimeOffset.Now;
        return AttentionTime.Compute(timeline, entries, AttentionSettings.Default, now < to ? now : to);
    }

    private IDisposable _Handle<TRequest, TResponse>(string action, Func<TRequest, Task<TResponse>> handle) =>
        host.Channel.Handle(action, async (payload, _) =>
        {
            var request = payload.Deserialize<TRequest>(ProductivityChannel.Json)
                ?? throw new InvalidOperationException($"The '{action}' request was empty.");
            return JsonSerializer.SerializeToElement(await handle(request).ConfigureAwait(false), ProductivityChannel.Json);
        });
}
