using Cockpit.Plugin.Productivity.Contracts;
using Cockpit.Plugins.Abstractions.TimeTracking;

namespace Cockpit.Plugin.Productivity.Tests;

// AC-1723 layer 2 (money): the hours a week proposal offers for billing.
public sealed class WeekProposalTests
{
    private static readonly DateOnly Week42 = new(2026, 10, 12);
    private static readonly DateOnly Week43 = new(2026, 10, 19);
    private static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>();

    [Theory]
    [InlineData(480, 0, true, "a:10,b:10,c:10", "a:150,b:165,c:165")]
    [InlineData(240, 270, false, "syn-portal:60", "")]
    public void Build_SplitsTheScheduleLessOwnEntriesByMeasuredTimeAndNeverExceedsIt(int schedule, int own, bool pastRetention, string measured, string expected)
    {
        // Past the raw retention the measured values are operator actions from the day totals, not minutes.
        var projects = _Minutes(measured);
        IReadOnlyList<ProjectTime> measuredTime = pastRetention
            ? AttentionTime.MeasuredFromDays(projects.Select(pair => new CockpitTimelineDay("pane", Week43, pair.Key, null, [], TimeSpan.Zero, TimeSpan.Zero, pair.Value, 0, 0, 0, 0, 0, 0, null)), AttentionSettings.Default)
            : [.. projects.Select(pair => new ProjectTime(pair.Key, TimeSpan.FromMinutes(pair.Value)))];
        var day = new WeekDayInput(
            Week43,
            [_Entry("Weekstart met het team", Week43, own, "syn-portal", CockpitTimeEntryStatus.Open)],
            measuredTime,
            new Dictionary<string, string[]>());
        var settings = WeekSettings.Default with { ScheduleMinutes = [schedule, 0, 0, 0, 0, 0, 0] };

        var proposal = WeekProposal.Build([day], settings, [], Names, []);

        Assert.Equal(
            _Minutes(expected),
            proposal.Lines.Where(line => line.Own == false).ToDictionary(line => line.ProjectId ?? string.Empty, line => (int)line.Cells.Single().Time.TotalMinutes));
        var total = proposal.Days.Single();
        Assert.Equal(TimeSpan.FromMinutes(Math.Max(schedule, own)), total.Total);
        Assert.Equal(own > schedule, total.OverSchedule);
    }

    [Fact]
    public void OpenLines_LeaveBookedAndPrivateOutKeepEditsAndWarnAboveTheSchedule()
    {
        var days = Enumerable.Range(0, 5).Select(offset => Week42.AddDays(offset)).ToArray();
        var entries = new[]
        {
            _Entry("Sprintplanning", days[0], 90, "syn-portal", CockpitTimeEntryStatus.Booked),
            _Entry("Overleg klant YYY", days[1], 30, null, CockpitTimeEntryStatus.Booked),
            _Entry("Demo nieuwe module", days[3], 60, "syn-api", CockpitTimeEntryStatus.Open) with { OutcomeReason = "boven het schema" },
            _Entry("Nabellen Acme", days[4], 30, "syn-portal", CockpitTimeEntryStatus.Open),
        };
        int[][] booked = [[240, 150], [285, 165], [300, 180], [0, 0], [150, 90]];
        var frozen = days.Where((_, index) => index != 3).SelectMany(day => new[]
        {
            new ProposalCell(day, TimeSpan.FromMinutes(booked[day.DayNumber - Week42.DayNumber][0]), CockpitTimeEntryStatus.Booked, "syn-portal"),
            new ProposalCell(day, TimeSpan.FromMinutes(booked[day.DayNumber - Week42.DayNumber][1]), CockpitTimeEntryStatus.Booked, "syn-api"),
        }).ToList();
        var input = days.Select(day => new WeekDayInput(
            day,
            [.. entries.Where(entry => DateOnly.FromDateTime(entry.StartedAt.LocalDateTime) == day)],
            [new ProjectTime("syn-portal", TimeSpan.FromMinutes(200)), new ProjectTime("syn-api", TimeSpan.FromMinutes(150)), new ProjectTime("ai-cockpit", TimeSpan.FromMinutes(90))],
            new Dictionary<string, string[]>())).ToList();
        var settings = WeekSettings.Default with { PrivateProjects = ["ai-cockpit"] };

        // An edit holds on an open cell and the rest of the day goes to the other project; on a booked cell it is ignored.
        CellOverride[] edits = [new(days[3], "syn-api", 210, "SYN-390 demo"), new(days[4], "syn-portal", 60, null)];

        var proposal = WeekProposal.Build(input, settings, frozen, Names, edits);

        var open = proposal.OpenLines;
        Assert.Equal(new[] { "Demo nieuwe module", "Nabellen Acme", "syn-portal", "syn-api" }, open.Select(line => line.Label));
        Assert.Equal(new[] { days[3] }, open.Where(line => line.Own == false).SelectMany(line => line.Cells).Select(cell => cell.Day).Distinct());
        Assert.Equal(new[] { 210, 210 }, open.Where(line => line.Own == false).Select(line => (int)line.Cells.Single().Time.TotalMinutes));
        Assert.DoesNotContain(proposal.Lines, line => line.ProjectId == "ai-cockpit");
        Assert.All(proposal.Days, day => Assert.Equal(TimeSpan.FromMinutes(440), day.Measured));
        Assert.Equal(new[] { days[4] }, proposal.Days.Where(day => day.OverSchedule).Select(day => day.Day));
        Assert.Equal(TimeSpan.FromMinutes(270), proposal.Days[4].Total);
    }

    private static CockpitTimeEntry _Entry(string description, DateOnly day, int minutes, string? project, CockpitTimeEntryStatus status) =>
        new(Guid.NewGuid(), description, new DateTimeOffset(day.ToDateTime(new TimeOnly(9, 0))), TimeSpan.FromMinutes(minutes), project, CockpitTimeEntrySource.Duration, status);

    private static Dictionary<string, int> _Minutes(string pairs) =>
        pairs.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split(':'))
            .ToDictionary(parts => parts[0], parts => int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
}
