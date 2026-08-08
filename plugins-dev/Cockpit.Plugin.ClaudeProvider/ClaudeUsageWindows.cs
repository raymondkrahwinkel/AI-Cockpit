using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.ClaudeProvider;

// Reads the rolling allowances out of a `get_usage` control-response. Replaces AC-549's `.claude.json` →
// `cachedUsageUtilization` route, which leaned on `claude -p "/usage"` to refresh the file — on 2.1.226 that
// became a real assistant turn (35.8s) that left the cache untouched. No subprocess, no tokens, nothing to
// cache, no freshness limit to get wrong.
//
// Shape, verbatim from a live 2.1.226 session:
// `{"rate_limits":{"five_hour":{"utilization":7,"resets_at":"2026-08-08T18:00:00.978410+00:00"}, …}}`
internal static class ClaudeUsageWindows
{
    // Keyed by the same wire names `rate_limit_event` uses, so both sources land in one dictionary. Empty for a
    // reply that names none — never a zero standing in for "unknown".
    public static IReadOnlyDictionary<string, PluginRateLimitWindow> Read(JsonElement response)
    {
        var windows = new Dictionary<string, PluginRateLimitWindow>(StringComparer.Ordinal);

        if (response.ValueKind != JsonValueKind.Object
            || !response.TryGetProperty("rate_limits", out var rateLimits)
            || rateLimits.ValueKind != JsonValueKind.Object)
        {
            return windows;
        }

        _Add(windows, rateLimits, ClaudeUsageSignals.FiveHourWireType);
        _Add(windows, rateLimits, ClaudeUsageSignals.WeeklyWireType);
        return windows;
    }

    private static void _Add(Dictionary<string, PluginRateLimitWindow> windows, JsonElement rateLimits, string wireType)
    {
        if (!rateLimits.TryGetProperty(wireType, out var window)
            || window.ValueKind != JsonValueKind.Object
            || !window.TryGetProperty("utilization", out var percent)
            || percent.ValueKind != JsonValueKind.Number
            || !percent.TryGetDouble(out var usedPercent)
            || !double.IsFinite(usedPercent)
            || usedPercent < 0)
        {
            return;
        }

        // ⚠️ Already a percentage here (measured: 7 for an account at 7%), where the same word on
        // `rate_limit_event` is a fraction that has to be multiplied by 100. Two shapes of one field; scaling this
        // one too would report a 7% window as 700% full.
        windows[wireType] = new PluginRateLimitWindow(
            ClaudeUsageSignals.WindowLabel(wireType),
            usedPercent,
            _ResetsAt(window),
            WindowMinutes: null);
    }

    // resets_at is an ISO-8601 string here — not the epoch seconds `rate_limit_event` uses for the same idea.
    private static DateTimeOffset? _ResetsAt(JsonElement window) =>
        window.TryGetProperty("resets_at", out var resetsAt)
        && resetsAt.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(resetsAt.GetString(), out var parsed)
            ? parsed
            : null;
}
