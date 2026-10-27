using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.CliAgentProvider;

// The app-server uses this shape for both account/rateLimits/read and rateLimits/updated.
internal static class CodexRateLimits
{
    public static IReadOnlyList<PluginRateLimitWindow> Parse(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty("rateLimits", out var snapshot) || snapshot.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var windows = new List<PluginRateLimitWindow>(2);
        if (_ParseWindow(snapshot, "primary") is { } primary)
        {
            windows.Add(primary);
        }

        if (_ParseWindow(snapshot, "secondary") is { } secondary)
        {
            windows.Add(secondary);
        }

        return windows;
    }

    private static PluginRateLimitWindow? _ParseWindow(JsonElement snapshot, string property)
    {
        if (!snapshot.TryGetProperty(property, out var window) || window.ValueKind != JsonValueKind.Object
            || _TryGetDouble(window, "usedPercent") is not { } usedPercent)
        {
            return null;
        }

        var windowMinutes = _TryGetInt(window, "windowDurationMins");
        var resetsAt = _TryGetLong(window, "resetsAt") is { } epochSeconds
            ? DateTimeOffset.FromUnixTimeSeconds(epochSeconds)
            : (DateTimeOffset?)null;

        return new PluginRateLimitWindow(CodexUsageSignals.WindowLabel(windowMinutes), usedPercent, resetsAt, windowMinutes);
    }

    private static int? _TryGetInt(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(property, out var element)
        && element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value)
            ? value
            : null;

    private static long? _TryGetLong(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(property, out var element)
        && element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var value)
            ? value
            : null;

    private static double? _TryGetDouble(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(property, out var element)
        && element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var value)
            ? value
            : null;
}
