using System.Text.Json;
using Cockpit.Plugins.Abstractions.Projects;
using Cockpit.Plugins.Abstractions.TimeTracking;

namespace Cockpit.Plugin.Productivity.Contracts;

internal static class ProductivityChannel
{
    public const string Snapshot = "productivity.time.snapshot";
    public const string Overview = "productivity.overview.snapshot";
    public const string Add = "productivity.time.add";
    public const string Update = "productivity.time.update";
    public const string Delete = "productivity.time.delete";
    public const string StartStopwatch = "productivity.time.stopwatch.start";
    public const string UpdateStopwatch = "productivity.time.stopwatch.update";
    public const string StopStopwatch = "productivity.time.stopwatch.stop";
    public const string Week = "productivity.time.week";

    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web);
}

internal sealed record ProductivitySnapshotRequest(
    DateTimeOffset From,
    DateTimeOffset To,
    DateTimeOffset TodayFrom,
    DateTimeOffset TodayTo);

// TodayTotal is your time today (measured plus own entries, overlap once); Day is the selected day's attention.
internal sealed record ProductivitySnapshot(
    IReadOnlyList<CockpitTimeEntry> Entries,
    TimeSpan TodayTotal,
    CockpitRunningStopwatch? Stopwatch,
    AttentionTime Day);

internal sealed record ProductivityOverviewRequest(DateTimeOffset From, DateTimeOffset To);

internal sealed record ProductivityOverviewSnapshot(
    AttentionTime Attention,
    IReadOnlyList<CockpitTimeEntry> Entries,
    CockpitTimeline Timeline,
    IReadOnlyList<PluginProjectInfo> Projects)
{
    public TimeSpan YourTime => Attention.YourTime;
    public TimeSpan WithAgentWithoutYou => Attention.YourTime + Attention.AgentWithoutYou;
}

internal sealed record ProductivitySaveRequest(
    Guid? Id,
    string Description,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    string? ProjectId);

// DayStarts holds the eight local midnights from Monday to the next Monday.
internal sealed record ProductivityWeekRequest(IReadOnlyList<DateTimeOffset> DayStarts);

internal sealed record ProductivityDeleteRequest(Guid Id);

internal sealed record ProductivityStopwatchRequest(string Description, string? ProjectId);

internal sealed record ProductivityAcknowledgement(bool Success);
