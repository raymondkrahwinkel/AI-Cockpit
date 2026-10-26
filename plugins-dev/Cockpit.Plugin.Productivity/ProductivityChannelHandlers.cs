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
        return ProductivityOverview.Build(
            request,
            timeline,
            entries,
            projects,
            host.Sessions.OpenSessions,
            settings,
            host.Storage.Get<Dictionary<string, double>>(ProfileRates.StorageKey) ?? [],
            DateTimeOffset.Now);
    }

    private async Task<ProductivitySnapshot> _SnapshotAsync(ProductivitySnapshotRequest request)
    {
        var entries = await timeEntries.ListAsync(request.From, request.To).ConfigureAwait(false);
        var todayEntries = request.From == request.TodayFrom && request.To == request.TodayTo
            ? entries
            : await timeEntries.ListAsync(request.TodayFrom, request.TodayTo).ConfigureAwait(false);
        var stopwatch = await timeEntries.GetRunningStopwatchAsync().ConfigureAwait(false);
        var (day, timeline) = await _AttentionAsync(request.From, request.To, entries).ConfigureAwait(false);
        var today = ReferenceEquals(todayEntries, entries)
            ? day
            : (await _AttentionAsync(request.TodayFrom, request.TodayTo, todayEntries).ConfigureAwait(false)).Attention;
        var projects = await host.GetProjectsAsync().ConfigureAwait(false);
        return new ProductivitySnapshot(entries, today.YourTime, stopwatch, day, projects, SessionLabel.From(timeline, host.Sessions.OpenSessions));
    }

    private async Task<WeekProposal> _WeekAsync(ProductivityWeekRequest request)
    {
        var settings = host.Storage.Get<WeekSettings>(WeekSettings.StorageKey) ?? WeekSettings.Default;
        var projects = await host.GetProjectsAsync().ConfigureAwait(false);
        var sessions = new List<SessionLabel>();
        var days = new List<WeekDayInput>();
        for (var index = 0; index + 1 < request.DayStarts.Count; index++)
        {
            var (from, to) = (request.DayStarts[index], request.DayStarts[index + 1]);
            var entries = await timeEntries.ListAsync(from, to).ConfigureAwait(false);
            var timeline = await host.Timeline.QueryAsync(from, to).ConfigureAwait(false);
            var now = DateTimeOffset.Now;
            var attention = AttentionTime.Compute(timeline, entries, AttentionSettings.Default, now < to ? now : to);
            sessions.AddRange(SessionLabel.From(timeline, host.Sessions.OpenSessions));
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

        // Every stored value gets the name it resolves to, a folder from before AC-1737 included.
        var resolver = new ProjectNames(projects, sessions);
        var names = days.SelectMany(day => day.Entries.Select(entry => entry.ProjectId).Concat(day.Measured.Select(project => project.ProjectId)))
            .OfType<string>()
            .Concat(projects.Select(project => project.Id))
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(value => value, value => resolver.Resolve(value).Label, StringComparer.Ordinal);
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
    private async Task<(AttentionTime Attention, CockpitTimeline Timeline)> _AttentionAsync(DateTimeOffset from, DateTimeOffset to, IReadOnlyList<CockpitTimeEntry> entries)
    {
        var timeline = await host.Timeline.QueryAsync(from, to).ConfigureAwait(false);
        var now = DateTimeOffset.Now;
        return (AttentionTime.Compute(timeline, entries, AttentionSettings.Default, now < to ? now : to), timeline);
    }

    private IDisposable _Handle<TRequest, TResponse>(string action, Func<TRequest, Task<TResponse>> handle) =>
        host.Channel.Handle(action, async (payload, _) =>
        {
            var request = payload.Deserialize<TRequest>(ProductivityChannel.Json)
                ?? throw new InvalidOperationException($"The '{action}' request was empty.");
            return JsonSerializer.SerializeToElement(await handle(request).ConfigureAwait(false), ProductivityChannel.Json);
        });
}
