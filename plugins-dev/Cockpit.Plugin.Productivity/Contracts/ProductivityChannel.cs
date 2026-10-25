using System.Text.Json;

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
