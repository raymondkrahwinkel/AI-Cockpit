using System.Text.Json;
using Cockpit.Plugins.Abstractions.TimeTracking;

namespace Cockpit.Plugin.Productivity.Contracts;

internal static class ProductivityChannel
{
    public const string Snapshot = "productivity.time.snapshot";
    public const string Add = "productivity.time.add";
    public const string Update = "productivity.time.update";
    public const string Delete = "productivity.time.delete";
    public const string StartStopwatch = "productivity.time.stopwatch.start";
    public const string UpdateStopwatch = "productivity.time.stopwatch.update";
    public const string StopStopwatch = "productivity.time.stopwatch.stop";

    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web);
}

internal sealed record ProductivitySnapshotRequest(
    DateTimeOffset From,
    DateTimeOffset To,
    DateTimeOffset TodayFrom,
    DateTimeOffset TodayTo);

internal sealed record ProductivitySnapshot(
    IReadOnlyList<CockpitTimeEntry> Entries,
    TimeSpan TodayTotal,
    CockpitRunningStopwatch? Stopwatch);

internal sealed record ProductivitySaveRequest(
    Guid? Id,
    string Description,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    string? ProjectId);

internal sealed record ProductivityDeleteRequest(Guid Id);

internal sealed record ProductivityStopwatchRequest(string Description, string? ProjectId);

internal sealed record ProductivityAcknowledgement(bool Success);
