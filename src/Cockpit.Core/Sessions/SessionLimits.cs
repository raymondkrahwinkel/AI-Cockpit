using System.Text.Json;

namespace Cockpit.Core.Sessions;

/// <summary>
/// What a session is spending: how full its context window is, and how much of the operator's five-hour and
/// weekly allowance is gone.
/// </summary>
/// <param name="ContextUsedPercent">How full the context window is, 0-100. Null when Claude has not said yet (before the first turn, and right after a /compact).</param>
/// <param name="FiveHourUsedPercent">The five-hour session allowance, 0-100.</param>
/// <param name="FiveHourResetsAt">When that window rolls over.</param>
/// <param name="SevenDayUsedPercent">The weekly allowance, 0-100.</param>
/// <param name="SevenDayResetsAt">When the week rolls over.</param>
public sealed record SessionLimits(
    double? ContextUsedPercent,
    double? FiveHourUsedPercent,
    DateTimeOffset? FiveHourResetsAt,
    double? SevenDayUsedPercent,
    DateTimeOffset? SevenDayResetsAt)
{
    /// <summary>Whether there is anything worth showing. Before the first response Claude reports none of it, and a header that says "0%" would be a claim we cannot make.</summary>
    public bool HasAny => ContextUsedPercent is not null || FiveHourUsedPercent is not null || SevenDayUsedPercent is not null;

    /// <summary>
    /// Reads the JSON that Claude Code hands its statusline command on stdin.
    /// <para>
    /// This is the only machine-readable source for the five-hour and weekly limits: they reach Claude Code in
    /// response headers the cockpit never sees, and they appear in no transcript, no session file and no CLI
    /// subcommand (checked against 2.1.209). The context percentage is served here pre-computed, which is also
    /// the reason not to add it up from the transcript's token counts ourselves — that sum is what a turn
    /// <em>cost</em>, not how full the window <em>is</em>.
    /// </para>
    /// <para>
    /// Everything is optional on purpose: <c>rate_limits</c> only exists on a subscription and only after the
    /// first response, and the shape has moved between versions. A missing field is a field we do not show, not
    /// an error — a status bar is not worth a crash.
    /// </para>
    /// </summary>
    public static SessionLimits? TryParse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var context = Percent(root, "context_window", "used_percentage");
            var (fiveHour, fiveHourResets) = Window(root, "five_hour");
            var (sevenDay, sevenDayResets) = Window(root, "seven_day");

            return new SessionLimits(context, fiveHour, fiveHourResets, sevenDay, sevenDayResets);
        }
        catch (JsonException)
        {
            // A half-written file caught mid-flush. The next refresh brings a whole one.
            return null;
        }
    }

    private static (double? Used, DateTimeOffset? ResetsAt) Window(JsonElement root, string name)
    {
        if (!root.TryGetProperty("rate_limits", out var limits)
            || limits.ValueKind != JsonValueKind.Object
            || !limits.TryGetProperty(name, out var window)
            || window.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }

        var used = window.TryGetProperty("used_percentage", out var percentage) && percentage.ValueKind == JsonValueKind.Number
            ? percentage.GetDouble()
            : (double?)null;

        var resetsAt = window.TryGetProperty("resets_at", out var resets) && resets.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(resets.GetString(), out var parsed)
                ? parsed
                : (DateTimeOffset?)null;

        return (used, resetsAt);
    }

    private static double? Percent(JsonElement root, string section, string field) =>
        root.TryGetProperty(section, out var element)
        && element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(field, out var value)
        && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;
}
