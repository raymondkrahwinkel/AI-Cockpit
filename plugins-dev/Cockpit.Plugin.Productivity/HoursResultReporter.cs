using Cockpit.Plugin.Productivity.Contracts;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.TimeTracking;

namespace Cockpit.Plugin.Productivity;

internal sealed class HoursResultReporter(ICockpitHost host, SemaphoreSlim? reportGate = null)
{
    private readonly SemaphoreSlim _gate = reportGate ?? new(1, 1);

    public async Task<WeekReport> ReportAsync(HoursResultRequest request, CancellationToken cancellationToken = default)
    {
        var caller = host.CurrentMcpCaller;
        if (caller is not { Kind: McpCallerKind.Session } || string.IsNullOrWhiteSpace(caller.PaneId))
        {
            throw new UnauthorizedAccessException("Only the verified recipient of the hours proposal may report.");
        }

        if (request.Week.DayOfWeek != DayOfWeek.Monday || request.Items is not { Count: > 0 and <= 1000 } || request.AlreadyBooked is null)
        {
            throw new ArgumentException("Give the Monday of the week and 1 to 1000 results.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var key = WeekReport.StorageKey(request.Week);
            var report = host.Storage.Get<WeekReport>(key) ?? WeekReport.Empty;
            var rows = report.Rows.ToDictionary(row => row.Row.Marker, StringComparer.Ordinal);
            var markers = new HashSet<string>(StringComparer.Ordinal);
            var entries = await host.TimeEntries.ListAsync(_Local(request.Week), _Local(request.Week.AddDays(7)), cancellationToken).ConfigureAwait(false);
            foreach (var item in request.Items)
            {
                if (item is null || string.IsNullOrEmpty(item.Marker) || !rows.TryGetValue(item.Marker, out var row) || row.PaneId != caller.PaneId)
                {
                    throw new UnauthorizedAccessException("This marker does not belong to this recipient or offered week.");
                }

                if (!markers.Add(item.Marker) || item.Status is not ("booked" or "skipped") || item.Status == "skipped" && string.IsNullOrWhiteSpace(item.Reason))
                {
                    throw new ArgumentException("Use unique markers, booked or skipped, and a reason for skipped.");
                }

                if (row.Status is not null && (row.Status != item.Status || row.Reason != item.Reason))
                {
                    throw new InvalidOperationException("This result was already processed; offer the open row again first.");
                }

                if (row.PendingStatus is not null && (row.PendingStatus != item.Status || row.PendingReason != item.Reason))
                {
                    throw new InvalidOperationException("Repeat the interrupted result unchanged.");
                }

                if (row.Status is null && row.Row.EntryId is { } id)
                {
                    var entry = entries.SingleOrDefault(entry => entry.Id == id);
                    var canWrite = entry is { Status: CockpitTimeEntryStatus.Offered } && entry.OfferedToPaneId == caller.PaneId;
                    var recovered = row.PendingStatus == item.Status && entry is not null
                        && entry.Status == (item.Status == "booked" ? CockpitTimeEntryStatus.Booked : CockpitTimeEntryStatus.Open)
                        && entry.OutcomeReason == (string.IsNullOrWhiteSpace(item.Reason) ? null : item.Reason.Trim());
                    if (!canWrite && !recovered)
                    {
                        throw new UnauthorizedAccessException("The own row was not offered to this recipient or has since been changed.");
                    }
                }
            }

            if (request.AlreadyBooked.Any(day => day is null || day.Minutes < 0 || day.Minutes > 1000000 || day.Day < request.Week || day.Day >= request.Week.AddDays(7) || !request.Items.Any(item => rows[item.Marker].Row.Day == day.Day)) || request.AlreadyBooked.Select(day => day.Day).Distinct().Count() != request.AlreadyBooked.Count)
            {
                throw new ArgumentException("Give at most one non-negative number of already booked minutes per day.");
            }

            var already = request.AlreadyBooked.ToDictionary(day => day.Day, day => day.Minutes);
            var offers = request.Items.Select(item => rows[item.Marker].OfferId).ToHashSet();
            if (report.Rows.Any(row => offers.Contains(row.OfferId) && already.TryGetValue(row.Row.Day, out var minutes) && minutes < row.AlreadyBooked))
            {
                throw new InvalidOperationException("A repeated report may not lower already reported booked minutes.");
            }

            foreach (var item in request.Items)
            {
                var row = rows[item.Marker];
                if (row.Status is not null)
                {
                    continue;
                }

                row = row with { PendingStatus = item.Status, PendingReason = item.Reason, AlreadyBooked = already.GetValueOrDefault(row.Row.Day, row.AlreadyBooked) };
                rows[item.Marker] = row;
                report = report with { Rows = rows.Values.ToList() };
                host.Storage.Set(key, report);

                if (row.Row.EntryId is { } id)
                {
                    if (entries.Single(entry => entry.Id == id).Status == CockpitTimeEntryStatus.Offered)
                    {
                        await host.TimeEntries.SetOutcomeAsync([id], item.Status == "booked" ? CockpitTimeEntryOutcome.Booked : CockpitTimeEntryOutcome.Skipped, item.Reason, cancellationToken).ConfigureAwait(false);
                    }
                }
                else
                {
                    _ProjectOutcome(request.Week, row, item);
                }

                rows[item.Marker] = row with { Status = item.Status, Reason = item.Reason, PendingStatus = null, PendingReason = null };
                report = report with { Rows = rows.Values.ToList() };
                host.Storage.Set(key, report);
            }

            report = report with
            {
                Rows = rows.Values.Select(row => offers.Contains(row.OfferId) && already.TryGetValue(row.Row.Day, out var minutes) ? row with { AlreadyBooked = minutes } : row).ToList(),
                AgentName = host.Sessions.OpenSessions.FirstOrDefault(session => session.PaneId == caller.PaneId)?.Name ?? caller.PaneId,
                ReportedAt = DateTimeOffset.Now,
                BookedCount = request.Items.Count(item => item.Status == "booked"),
                BookedDays = request.Items.Where(item => item.Status == "booked").Select(item => rows[item.Marker].Row.Day).Distinct().Order().ToList(),
                AlreadyBookedDescriptions = report.AlreadyBookedDescriptions
                    .Where(pair => request.AlreadyBooked.All(day => day.Day != pair.Key || string.IsNullOrWhiteSpace(day.Description)))
                    .Concat(request.AlreadyBooked.Where(day => !string.IsNullOrWhiteSpace(day.Description)).Select(day => new KeyValuePair<DateOnly, string>(day.Day, day.Description ?? string.Empty)))
                    .ToDictionary(pair => pair.Key, pair => pair.Value),
            };
            host.Storage.Set(key, report);
            host.Channel.Publish("productivity.hours-reported", System.Text.Json.JsonSerializer.SerializeToElement(new { week = request.Week }, ProductivityChannel.Json));
            return report;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void _ProjectOutcome(DateOnly week, WeekReportedRow row, HoursResultItem item)
    {
        var key = FrozenProjectCell.StorageKey(week);
        var frozen = host.Storage.Get<FrozenProjectCell[]>(key) ?? [];
        host.Storage.Set(key, frozen.Select(cell => cell.Marker == item.Marker ? cell with { Status = item.Status == "booked" ? CockpitTimeEntryStatus.Booked : CockpitTimeEntryStatus.Open, OutcomeReason = item.Reason } : cell).ToArray());
        if (item.Status == "skipped" && row.Row.ProjectId is { } project)
        {
            var editsKey = CellOverride.StorageKey(week);
            var edits = host.Storage.Get<CellOverride[]>(editsKey) ?? [];
            var description = frozen.FirstOrDefault(cell => cell.Marker == item.Marker)?.Description
                ?? edits.FirstOrDefault(edit => edit.Day == row.Row.Day && edit.ProjectId == project)?.Description;
            host.Storage.Set<CellOverride[]>(editsKey, [.. edits.Where(edit => edit.Day != row.Row.Day || edit.ProjectId != project), new(row.Row.Day, project, row.Row.Minutes, description)]);
        }
    }

    private static DateTimeOffset _Local(DateOnly day) => new(day.ToDateTime(TimeOnly.MinValue));
}
