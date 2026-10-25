using System.Globalization;
using Cockpit.Plugins.Abstractions.TimeTracking;

namespace Cockpit.Plugin.Productivity.Contracts;

// Schedule in minutes, Monday first; private projects count as your time but stay out of the proposal.
internal sealed record WeekSettings(int[] ScheduleMinutes, int RoundingMinutes, string[] PrivateProjects)
{
    public const string StorageKey = "week";

    public static WeekSettings Default { get; } = new([480, 480, 480, 480, 240, 0, 0], 15, []);

    public TimeSpan Schedule(DateOnly day) => TimeSpan.FromMinutes(ScheduleMinutes[((int)day.DayOfWeek + 6) % 7]);
}

// One day's input: own entries, measured time per project (AC-1731) and the tickets worked on per project.
internal sealed record WeekDayInput(
    DateOnly Day,
    IReadOnlyList<CockpitTimeEntry> Entries,
    IReadOnlyList<ProjectTime> Measured,
    IReadOnlyDictionary<string, string[]> Tickets);

// One day of a line. As input, a project cell already offered or booked: it stays as it was and is not redistributed.
internal sealed record ProposalCell(
    DateOnly Day,
    TimeSpan Time,
    CockpitTimeEntryStatus Status,
    string? ProjectId = null,
    DateTimeOffset? OfferedAt = null,
    string? OutcomeReason = null,
    string? Description = null,
    bool Edited = false);

// The operator's own value for an open project cell; null keeps the computed duration or the tickets of that day.
internal sealed record CellOverride(DateOnly Day, string ProjectId, int? Minutes, string? Description)
{
    public static string StorageKey(DateOnly weekStart) => $"week.{weekStart:yyyy-MM-dd}";
}

internal sealed record ProposalLine(string Label, string? ProjectId, Guid? EntryId, string Detail, IReadOnlyList<ProposalCell> Cells)
{
    public bool Own => EntryId is not null;

    public bool IsOpen => Cells.Any(cell => cell.Status == CockpitTimeEntryStatus.Open);
}

internal sealed record ProposalDay(DateOnly Day, TimeSpan Schedule, TimeSpan Total, TimeSpan Measured)
{
    public bool OverSchedule => Total > Schedule;
}

internal sealed record WeekProposal(IReadOnlyList<ProposalDay> Days, IReadOnlyList<ProposalLine> Lines)
{
    private static readonly CultureInfo Dutch = CultureInfo.GetCultureInfo("nl-NL");

    // What goes to an agent: the open lines with only their open days. Booked never goes again.
    public IReadOnlyList<ProposalLine> OpenLines =>
        [.. Lines.Where(line => line.IsOpen).Select(line => line with { Cells = [.. line.Cells.Where(cell => cell.Status == CockpitTimeEntryStatus.Open)] })];

    public static WeekProposal Build(
        IReadOnlyList<WeekDayInput> days,
        WeekSettings settings,
        IReadOnlyList<ProposalCell> frozen,
        IReadOnlyDictionary<string, string> projectNames,
        IReadOnlyList<CellOverride> overrides)
    {
        var isPrivate = settings.PrivateProjects.ToHashSet(StringComparer.Ordinal);
        var lines = new List<ProposalLine>();
        foreach (var entry in days.SelectMany(day => day.Entries).Where(entry => entry.ProjectId is null || isPrivate.Contains(entry.ProjectId) == false).OrderBy(entry => entry.StartedAt))
        {
            lines.Add(new ProposalLine(
                entry.Description,
                entry.ProjectId,
                entry.Id,
                entry.ProjectId is { } project ? _Name(project, projectNames) : "geen project",
                [new ProposalCell(DateOnly.FromDateTime(entry.StartedAt.LocalDateTime), entry.Duration, entry.Status, entry.ProjectId, entry.OfferedAt, entry.OutcomeReason)]));
        }

        var projectCells = new Dictionary<string, List<ProposalCell>>(StringComparer.Ordinal);
        var totals = new List<ProposalDay>();
        foreach (var day in days)
        {
            var schedule = settings.Schedule(day.Day);
            var own = _Sum(lines.Where(line => line.Own).SelectMany(line => line.Cells).Where(cell => cell.Day == day.Day).Select(cell => cell.Time));
            var kept = frozen.Where(cell => cell.Day == day.Day && cell.ProjectId is not null && cell.Status != CockpitTimeEntryStatus.Open).ToList();
            var dayOverrides = overrides
                .Where(edit => edit.Day == day.Day && isPrivate.Contains(edit.ProjectId) == false && kept.All(cell => cell.ProjectId != edit.ProjectId))
                .ToDictionary(edit => edit.ProjectId, StringComparer.Ordinal);
            var edited = dayOverrides.Values
                .Where(edit => edit.Minutes is > 0)
                .Select(edit => new ProposalCell(day.Day, TimeSpan.FromMinutes(edit.Minutes ?? 0), CockpitTimeEntryStatus.Open, edit.ProjectId, Edited: true))
                .ToList();

            // Edited cells hold their minutes; the rest of the day is split over the projects nobody edited.
            var free = schedule - own - _Sum(kept.Concat(edited).Select(cell => cell.Time));
            var weights = day.Measured
                .Where(project => project.ProjectId is { } id && isPrivate.Contains(id) == false && kept.All(cell => cell.ProjectId != id) && (dayOverrides.TryGetValue(id, out var edit) && edit.Minutes is not null) == false && project.Time > TimeSpan.Zero)
                .ToList();
            var shares = _Distribute(free > TimeSpan.Zero ? (int)free.TotalMinutes : 0, weights, settings.RoundingMinutes);
            var open = edited.Concat(shares.Select(share => new ProposalCell(day.Day, TimeSpan.FromMinutes(share.Minutes), CockpitTimeEntryStatus.Open, share.ProjectId)))
                .Select(cell => cell with
                {
                    Description = dayOverrides.TryGetValue(cell.ProjectId ?? string.Empty, out var edit) && edit.Description is { } text
                        ? text
                        : string.Join(", ", day.Tickets.TryGetValue(cell.ProjectId ?? string.Empty, out var worked) ? worked : []),
                })
                .ToList();
            foreach (var cell in kept.Concat(open))
            {
                var projectId = cell.ProjectId ?? string.Empty;
                if (projectCells.TryGetValue(projectId, out var cells))
                {
                    cells.Add(cell);
                }
                else
                {
                    projectCells[projectId] = [cell];
                }
            }

            totals.Add(new ProposalDay(
                day.Day,
                schedule,
                own + _Sum(kept.Concat(open).Select(cell => cell.Time)),
                _Sum(day.Measured.Select(project => project.Time))));
        }

        foreach (var (projectId, cells) in projectCells.OrderByDescending(pair => _Sum(pair.Value.Select(cell => cell.Time))))
        {
            var tickets = days.SelectMany(day => day.Tickets.TryGetValue(projectId, out var worked) ? worked : []).Distinct(StringComparer.Ordinal);
            lines.Add(new ProposalLine(_Name(projectId, projectNames), projectId, null, string.Join(", ", tickets), cells));
        }

        return new WeekProposal(totals, lines);
    }

    // The rows as shown, Monday to Sunday without the empty weekend days; Kopieer als tabel writes exactly this.
    public IReadOnlyList<string[]> Grid()
    {
        var days = VisibleDays;
        List<string[]> rows =
        [
            ["Regel", .. days.Select(day => day.Day.ToDateTime(TimeOnly.MinValue).ToString("ddd d", Dutch)), "Week", "Status"],
            ["Schema (grens)", .. days.Select(day => Text(day.Schedule)), Text(_Sum(days.Select(day => day.Schedule))), string.Empty],
        ];
        foreach (var line in Lines)
        {
            var label = line.Own ? $"eigen · {line.Label}" : line.Label;
            rows.Add([
                string.IsNullOrEmpty(line.Detail) ? label : $"{label} · {line.Detail}",
                .. days.Select(day => Cell(line, day.Day)),
                Text(_Sum(line.Cells.Select(cell => cell.Time))),
                Status(line),
            ]);
        }

        rows.Add(["Totaal voorstel", .. days.Select(day => Text(day.Total)), Text(_Sum(days.Select(day => day.Total))), string.Empty]);
        rows.Add(["gemeten", .. days.Select(day => Text(day.Measured)), Text(_Sum(days.Select(day => day.Measured))), string.Empty]);
        return rows;
    }

    public string ToTable() => string.Join(Environment.NewLine, Grid().Select(row => string.Join('\t', row)));

    public IReadOnlyList<ProposalDay> VisibleDays =>
        [.. Days.Where(day => day.Schedule > TimeSpan.Zero || day.Total > TimeSpan.Zero)];

    public static string Cell(ProposalLine line, DateOnly day) =>
        _Sum(line.Cells.Where(cell => cell.Day == day).Select(cell => cell.Time)) is var time && time > TimeSpan.Zero ? Text(time) : "—";

    public static string Text(TimeSpan time)
    {
        var minutes = (long)Math.Round(time.TotalMinutes);
        return $"{minutes / 60}:{minutes % 60:00}";
    }

    public static string Status(ProposalLine line)
    {
        if (line.Own)
        {
            var cell = line.Cells[0];
            return cell.Status switch
            {
                CockpitTimeEntryStatus.Booked => "geboekt",
                CockpitTimeEntryStatus.Offered => $"aangeboden op {cell.OfferedAt?.ToLocalTime().ToString("d-M", Dutch)}",
                _ when cell.OutcomeReason is { } reason => $"niet geboekt, {reason} · open",
                _ => "open",
            };
        }

        var groups = line.Cells
            .GroupBy(cell => cell.Status switch { CockpitTimeEntryStatus.Booked => "geboekt", CockpitTimeEntryStatus.Offered => "aangeboden", _ => "open" })
            .Select(group => $"{group.Key} {string.Join(", ", group.Select(cell => cell.Day.ToDateTime(TimeOnly.MinValue).ToString("ddd", Dutch)))}")
            .ToList();
        return groups.Count == 1 && line.Cells.All(cell => cell.Status == CockpitTimeEntryStatus.Open) ? "open" : string.Join(" · ", groups);
    }

    // Each project its measured share of the free minutes, rounded; the difference goes to the largest project first,
    // so the day lands exactly on the schedule and no share drops below zero.
    private static List<(string ProjectId, int Minutes)> _Distribute(int free, IReadOnlyList<ProjectTime> weights, int rounding)
    {
        var total = weights.Sum(project => project.Time.TotalMinutes);
        if (free == 0 || total <= 0)
        {
            return [];
        }

        var shares = weights
            .OrderByDescending(project => project.Time)
            .Select(project => (ProjectId: project.ProjectId ?? string.Empty, Minutes: (int)(Math.Round(free * project.Time.TotalMinutes / total / rounding, MidpointRounding.AwayFromZero) * rounding)))
            .ToList();
        var difference = free - shares.Sum(share => share.Minutes);
        for (var index = 0; index < shares.Count && difference != 0; index++)
        {
            var change = Math.Max(difference, -shares[index].Minutes);
            shares[index] = shares[index] with { Minutes = shares[index].Minutes + change };
            difference -= change;
        }

        return [.. shares.Where(share => share.Minutes > 0)];
    }

    private static string _Name(string projectId, IReadOnlyDictionary<string, string> names) =>
        names.TryGetValue(projectId, out var name) ? name : projectId;

    private static TimeSpan _Sum(IEnumerable<TimeSpan> values) => TimeSpan.FromTicks(values.Sum(value => value.Ticks));
}
