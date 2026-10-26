using System.Globalization;

namespace Cockpit.Plugin.Productivity.Contracts;

internal sealed record WeekOfferRow(DateOnly Day, int Sequence, int Minutes, Guid? EntryId, string? ProjectId)
{
    public string Marker => WeekOfferMarker.Format(ISOWeek.GetWeekOfYear(Day.ToDateTime(TimeOnly.MinValue)), Day, Sequence);
}

internal sealed record WeekReportedRow(WeekOfferRow Row, string PaneId, int ScheduleMinutes, string? Status = null, string? Reason = null, int AlreadyBooked = 0);
internal sealed record HoursResultItem(string Marker, string Status, string? Reason = null);
internal sealed record HoursAlreadyBooked(DateOnly Day, int Minutes);
internal sealed record HoursResultRequest(DateOnly Week, IReadOnlyList<HoursResultItem> Items, IReadOnlyList<HoursAlreadyBooked> AlreadyBooked);
internal sealed record HoursDayWarning(DateOnly Day, int ScheduleMinutes, int AlreadyBooked, int ProposedMinutes, bool Overrun, string PaneId, int BookedMinutes);

internal sealed record WeekReport(IReadOnlyList<WeekReportedRow> Rows, string? AgentName, DateTimeOffset? ReportedAt, int BookedCount)
{
    public static WeekReport Empty { get; } = new([], null, null, 0);
    public static string StorageKey(DateOnly week) => $"week-results.{week:yyyy-MM-dd}";

    public IReadOnlyList<HoursDayWarning> Warnings => Rows.GroupBy(row => row.Row.Day)
        .Where(group => group.Any(row => row.Status is not null))
        .Select(group => new HoursDayWarning(
            group.Key,
            group.Min(row => row.ScheduleMinutes),
            group.Max(row => row.AlreadyBooked),
            group.Sum(row => row.Row.Minutes),
            false,
            group.Last(row => row.Status is not null).PaneId,
            group.Where(row => row.Status == "booked").Sum(row => row.Row.Minutes)))
        .Where(day => day.Overrun || (long)day.AlreadyBooked + day.ProposedMinutes > day.ScheduleMinutes)
        .ToList();

    public bool IsGreen => ReportedAt is not null && Warnings.All(day => !day.Overrun);

    public string AgentStatus(Guid? entryId, string? projectId)
    {
        var rows = _Rows(entryId, projectId);
        if (rows.Any(row => row.Status == "booked" && Warnings.Any(day => day.Day == row.Row.Day && day.Overrun)))
        {
            return "⚠ overschrijding";
        }

        var booked = rows.Count(row => row.Status == "booked");
        if (entryId is null && booked > 0)
        {
            return $"✓ {booked} van {rows.Count} dagen";
        }

        return rows.Any(row => row.Status == "skipped") ? "⚠ niet geboekt" : booked > 0 ? "✓ geboekt" : string.Empty;
    }

    public string Reason(Guid? entryId, string? projectId) =>
        string.Join("; ", _Rows(entryId, projectId).Where(row => row.Reason is not null).Select(row => row.Reason).Distinct());

    private List<WeekReportedRow> _Rows(Guid? entryId, string? projectId) =>
        Rows.Where(row => entryId is not null ? row.Row.EntryId == entryId : row.Row.EntryId is null && row.Row.ProjectId == projectId).ToList();
}
