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
    ];

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
