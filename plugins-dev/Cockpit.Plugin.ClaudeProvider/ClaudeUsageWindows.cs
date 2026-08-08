using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.ClaudeProvider;

// Reads the rolling allowances out of the CLI's own `get_usage` control-response — the SDK route's source for
// the figures the TTY route gets from its statusline.
//
// This replaces the `.claude.json` → `cachedUsageUtilization` route AC-549 built. That route leaned on
// `claude -p "/usage"` to refresh the file, and on CLI 2.1.226 that stopped being a local answer: measured at
// 35.8s, a real assistant turn, and the cache left untouched. The control channel gives the same numbers with no
// subprocess, no tokens and no staleness window to guard — so there is nothing left to cache, and no
// `fetchedAtMs` freshness limit to get wrong.
//
// Shape, verbatim from a live 2.1.226 session:
// `{"subscription_type":"max","rate_limits_available":true,
//   "rate_limits":{"five_hour":{"utilization":7,"resets_at":"2026-08-08T18:00:00.978410+00:00"}, …}}`
internal static class ClaudeUsageWindows
{
    // The windows this reply vouches for, keyed by the same wire names `rate_limit_event` uses so the two
    // sources land in one dictionary. Empty for a reply that names none — never a guess, and never a zero
    // standing in for "unknown".
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
