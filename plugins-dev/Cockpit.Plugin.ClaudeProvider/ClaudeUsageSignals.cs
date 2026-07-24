using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.ClaudeProvider;

/// <summary>
/// What a Claude session can run out of, and how to read it from the statusline snapshot (AC-229). Both halves
/// live here rather than in the host for the same reason the transcript reader and the login gate do: the shape of
/// that JSON is Claude's business, it has moved between versions, and the core should carry no format knowledge of
/// any one provider.
/// <para>
/// The statusline is the only machine-readable source for the rolling limits: they reach Claude Code in response
/// headers the cockpit never sees, and they appear in no transcript, no session file and no CLI subcommand
/// (checked against 2.1.209). The context percentage is served here pre-computed, which is also the reason not to
/// add it up from the transcript's token counts — that sum is what a turn <em>cost</em>, not how full the window
/// <em>is</em>.
/// </para>
/// </summary>
public static class ClaudeUsageSignals
{
    /// <summary>The context window filling up. Drains on a compaction, so there is no moment to schedule against.</summary>
    public const string ContextKey = "context";

    /// <summary>The five-hour allowance.</summary>
    public const string FiveHourKey = "five-hour";

    /// <summary>The weekly allowance.</summary>
    public const string WeeklyKey = "weekly";

    private const string ResumePrompt = "continue";

    /// <summary>
    /// The three signals a Claude session reports, with the thresholds Raymond chose (2026-07-24): a context
    /// window worth mentioning at half full, and allowances worth mentioning when nearly gone. An operator can
    /// override any of them per provider, and a profile can override that again.
    /// </summary>
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

    /// <summary>
    /// Reads the JSON Claude Code hands its statusline command into readings for <see cref="Declarations"/>.
    /// Everything is optional on purpose: <c>rate_limits</c> exists only on a subscription and only after the
    /// first response, and the context percentage is silent before the first turn and right after a compaction. A
    /// missing figure is a figure not reported, never a zero — and a snapshot caught mid-flush yields nothing at
    /// all rather than throwing, because the next poll brings a whole one.
    /// </summary>
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
