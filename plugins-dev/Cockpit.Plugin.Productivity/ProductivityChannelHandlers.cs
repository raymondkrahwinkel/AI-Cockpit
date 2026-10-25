using System.Text.Json;
using Cockpit.Plugin.Productivity.Contracts;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.TimeTracking;

namespace Cockpit.Plugin.Productivity;

internal sealed class ProductivityChannelHandlers(ICockpitHost host, ICockpitTimeEntries timeEntries)
{
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
    ];

    private async Task<ProductivityOverviewSnapshot> _OverviewAsync(ProductivityOverviewRequest request)
    {
        var timeline = await host.Timeline.QueryAsync(request.From, request.To).ConfigureAwait(false);
        var entries = await timeEntries.ListAsync(request.From, request.To).ConfigureAwait(false);
        var projects = await host.GetProjectsAsync().ConfigureAwait(false);
        var now = DateTimeOffset.Now;
        var attention = AttentionTime.Compute(timeline, entries, AttentionSettings.Default, now < request.To ? now : request.To);
        return new ProductivityOverviewSnapshot(attention, entries, timeline, projects);
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

    // Frozen project cells have no store yet; AC-1725 offers them and decides where they live.
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

        var overrides = host.Storage.Get<CellOverride[]>(CellOverride.StorageKey(DateOnly.FromDateTime(request.DayStarts[0].LocalDateTime))) ?? [];
        return WeekProposal.Build(days, settings, [], names, overrides);
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
