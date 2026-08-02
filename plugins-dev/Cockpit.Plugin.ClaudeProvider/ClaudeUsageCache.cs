using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.ClaudeProvider;

// Reads the rolling allowances out of the CLI's own `.claude.json` — the SDK route's source for the figures
// the TTY route gets from its statusline (AC-549).
//
// The SDK route cannot have a statusline: measured against CLI 2.1.220 by handing `claude -p` a
// `--settings` with a relay of its own, the command is never invoked. And `rate_limit_event`, the other
// candidate, carries the window but not always its fill — captured from a real stream,
// `{"status":"allowed","resetsAt":…,"rateLimitType":"five_hour"}` arrives with no `utilization` field at
// all while the account is nowhere near that limit.
//
// What does exist on this route is `cachedUsageUtilization` in `.claude.json`, which the CLI refreshes
// when asked for `/usage` — a request it answers locally, measured at 0 tokens, 0ms of API time and $0. So
// the percentage a terminal session shows is reachable here too; it just has to be asked for.
internal static class ClaudeUsageCache
{
    // How old a cached reading may be before it is dropped rather than shown. This is not a tidiness limit: found
    // in the wild at *68 hours* stale, because nothing on the SDK route had ever refreshed it. Rendering
    // that as the current figure is exactly the invented number AC-530 forbids, and a stale allowance is worse
    // than an absent one — it reads as headroom the operator does not have.
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(15);

    // The windows this snapshot can vouch for, keyed by the same wire names `rate_limit_event` uses so the
    // two sources land in one dictionary. Empty for unreadable JSON, a missing section, or a snapshot older than
    // `MaxAge` — never a guess, and never a zero standing in for "unknown".
    public static IReadOnlyDictionary<string, PluginRateLimitWindow> Read(string json, DateTimeOffset now)
    {
        var windows = new Dictionary<string, PluginRateLimitWindow>(StringComparer.Ordinal);

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("cachedUsageUtilization", out var cached)
                || cached.ValueKind != JsonValueKind.Object
                || !_IsFresh(cached, now)
                || !cached.TryGetProperty("utilization", out var utilization)
                || utilization.ValueKind != JsonValueKind.Object)
            {
                return windows;
            }

            _Add(windows, utilization, ClaudeUsageSignals.FiveHourWireType);
            _Add(windows, utilization, ClaudeUsageSignals.WeeklyWireType);
        }
        catch (JsonException)
        {
            // A snapshot caught mid-write. The next refresh brings a whole one; a usage pill is never a reason to
            // fail a session.
        }

        return windows;
    }

    private static bool _IsFresh(JsonElement cached, DateTimeOffset now) =>
        cached.TryGetProperty("fetchedAtMs", out var fetchedAt)
        && fetchedAt.ValueKind == JsonValueKind.Number
        && fetchedAt.TryGetInt64(out var epochMs)
        && epochMs is >= 0 and <= 253402300799000
        && now - DateTimeOffset.FromUnixTimeMilliseconds(epochMs) is { } age
        && age >= TimeSpan.Zero
        && age <= MaxAge;

    private static void _Add(Dictionary<string, PluginRateLimitWindow> windows, JsonElement utilization, string wireType)
    {
        if (!utilization.TryGetProperty(wireType, out var window)
            || window.ValueKind != JsonValueKind.Object
            || !window.TryGetProperty("utilization", out var percent)
            || percent.ValueKind != JsonValueKind.Number
            || !percent.TryGetDouble(out var usedPercent)
            || !double.IsFinite(usedPercent)
            || usedPercent < 0)
        {
            return;
        }

        // ⚠️ Already a percentage here (measured: 2 and 7 for an account at 2% and 7%), where the same word on
        // rate_limit_event is a fraction that has to be multiplied by 100. Two shapes of one field; scaling this
        // one too would report a 2% window as 200% full.
        windows[wireType] = new PluginRateLimitWindow(
            ClaudeUsageSignals.WindowLabel(wireType),
            usedPercent,
            _ResetsAt(window),
            WindowMinutes: null);
    }

    // resets_at is an ISO-8601 string here — not the epoch seconds rate_limit_event uses for the same idea.
    private static DateTimeOffset? _ResetsAt(JsonElement window) =>
        window.TryGetProperty("resets_at", out var resetsAt)
        && resetsAt.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(resetsAt.GetString(), out var parsed)
            ? parsed
            : null;
}
