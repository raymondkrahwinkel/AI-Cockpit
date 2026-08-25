using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.ClaudeProvider;

// What a Claude session can run out of, read from the statusline snapshot (AC-229); lives here since the
// JSON shape is Claude's business and moves between versions. For a TTY session the statusline is the only
// machine-readable source (checked against 2.1.209); the SDK route reads its own stdout instead (AC-530).
public static class ClaudeUsageSignals
{
    // The context window filling up. Drains on a compaction, so there is no moment to schedule against.
    public const string ContextKey = "context";

    // The five-hour allowance.
    public const string FiveHourKey = "five-hour";

    // The weekly allowance.
    public const string WeeklyKey = "weekly";

    private const string ResumePrompt = "continue";

    // The three signals a Claude session reports, with the chosen thresholds: a context
    // window worth mentioning at half full, and allowances worth mentioning when nearly gone. An operator can
    // override any of them per provider, and a profile can override that again.
    public static IReadOnlyList<PluginUsageSignal> Declarations { get; } =
    [
        new(ContextKey, "ctx", PluginUsageSignalKind.Fill, DefaultThresholdPercent: 50)
        {
            Description = "Context window",
        },
        new(FiveHourKey, "5h", PluginUsageSignalKind.Allowance, DefaultThresholdPercent: 90)
        {
            Description = "Session (5 hours)",
            SupportsResume = true,
            DefaultResumePrompt = ResumePrompt,
        },
        new(WeeklyKey, "wk", PluginUsageSignalKind.Allowance, DefaultThresholdPercent: 90)
        {
            Description = "Week",
            SupportsResume = true,
            DefaultResumePrompt = ResumePrompt,
        },
    ];

    // The wire name of the five-hour window on the SDK route's `rate_limit_event` line. The statusline spells
    // it the same way; only the shape around it differs.
    public const string FiveHourWireType = "five_hour";

    // The wire name of the weekly window — `seven_day` on both routes, "wk" once it reaches a header.
    public const string WeeklyWireType = "seven_day";

    // Shared vocabulary so the SDK route spells "5h"/"wk" exactly as the statusline route does. A window this
    // build has no declaration for passes through under its own wire name rather than being dropped.
    public static string WindowLabel(string wireType) => wireType switch
    {
        FiveHourWireType => _LabelFor(FiveHourKey),
        WeeklyWireType => _LabelFor(WeeklyKey),
        _ => wireType,
    };

    private static string _LabelFor(string key) =>
        Declarations.First(declaration => string.Equals(declaration.Key, key, StringComparison.Ordinal)).Label;

    // Reads the JSON Claude Code hands its statusline command. Everything is optional on purpose: `rate_limits`
    // exists only on a subscription and after the first response, and context is silent before the first turn
    // or right after a compaction. A missing figure is never a zero; a mid-flush snapshot yields nothing.
    public static IReadOnlyList<PluginUsageReading> Read(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            var readings = new List<PluginUsageReading>(3);

            if (_Percent(root, "context_window", "used_percentage") is { } context)
            {
                readings.Add(new PluginUsageReading(ContextKey, context, ResetsAt: null));
            }

            _AddWindow(readings, root, "five_hour", FiveHourKey);
            _AddWindow(readings, root, "seven_day", WeeklyKey);

            return readings;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void _AddWindow(List<PluginUsageReading> readings, JsonElement root, string name, string signalKey)
    {
        if (!root.TryGetProperty("rate_limits", out var limits)
            || limits.ValueKind != JsonValueKind.Object
            || !limits.TryGetProperty(name, out var window)
            || window.ValueKind != JsonValueKind.Object
            || !window.TryGetProperty("used_percentage", out var percentage)
            || percentage.ValueKind != JsonValueKind.Number)
        {
            return;
        }

        readings.Add(new PluginUsageReading(signalKey, percentage.GetDouble(), _ResetsAt(window)));
    }

    // resets_at is a Unix epoch (seconds) number in the statusline JSON — e.g. "resets_at":1784415000 — not an ISO
    // string (verified against 2.1.209). Parse the number; keep string/ISO parsing as a fallback in case a version
    // sends it that way, so the reset time survives a shape change rather than vanishing.
    private static DateTimeOffset? _ResetsAt(JsonElement window)
    {
        if (!window.TryGetProperty("resets_at", out var resets))
        {
            return null;
        }

        // Range-guard the epoch: FromUnixTimeSeconds throws outside year 1..9999, and a garbled resets_at must
        // cost the reset time, not the reading it belongs to.
        const long minEpochSeconds = -62135596800; // 0001-01-01
        const long maxEpochSeconds = 253402300799; // 9999-12-31

        if (resets.ValueKind == JsonValueKind.Number
            && resets.TryGetInt64(out var epochSeconds)
            && epochSeconds is >= minEpochSeconds and <= maxEpochSeconds)
        {
            return DateTimeOffset.FromUnixTimeSeconds(epochSeconds);
        }

        return resets.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(resets.GetString(), out var parsed)
            ? parsed
            : null;
    }

    private static double? _Percent(JsonElement root, string section, string field) =>
        root.TryGetProperty(section, out var element)
        && element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(field, out var value)
        && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;
}
