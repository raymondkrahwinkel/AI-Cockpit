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
    /// The hover text for the header's limit bars: what each bar means, spelled out, plus when each window rolls
    /// over — the one thing a bar cannot show and the thing you want when it is nearly full. Only the numbers the
    /// provider reported, so nothing here is invented. Shared by every session header that renders limits,
    /// whatever provider fed them.
    /// </summary>
    public string Describe()
    {
        var lines = new List<string>(3);

        if (ContextUsedPercent is { } context)
        {
            lines.Add($"Context window: {Rounded(context)}% used");
        }

        if (FiveHourUsedPercent is { } fiveHour)
        {
            lines.Add($"Session (5 hours): {Rounded(fiveHour)}% used{Resets(FiveHourResetsAt)}");
        }

        if (SevenDayUsedPercent is { } sevenDay)
        {
            lines.Add($"Week: {Rounded(sevenDay)}% used{Resets(SevenDayResetsAt)}");
        }

        return string.Join(Environment.NewLine, lines);

        static string Resets(DateTimeOffset? resetsAt) =>
            resetsAt is { } at ? $" — resets {at.ToLocalTime():ddd HH:mm}" : string.Empty;

        // Away from zero, not .NET's default banker's rounding — which turns 42.5% into 42% and would have the
        // header quietly under-report exactly on the halves.
        static double Rounded(double value) => Math.Round(value, MidpointRounding.AwayFromZero);
    }

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

        // resets_at is a Unix epoch (seconds) number in the statusline JSON — e.g. "resets_at":1784415000 — not an
        // ISO string (verified against 2.1.209). Parse the number; keep string/ISO parsing as a fallback in case an
        // older or future version sends it that way, so the reset time survives a shape change rather than vanishing.
        DateTimeOffset? resetsAt = null;
        if (window.TryGetProperty("resets_at", out var resets))
        {
            // Range-guard the epoch: DateTimeOffset.FromUnixTimeSeconds throws ArgumentOutOfRangeException outside
            // year 1..9999, and that would escape this method's JsonException-only catch — a crafted/garbled
            // resets_at must leave the reset time null (and keep the used_percentage), not throw and stall the poll.
            const long minEpochSeconds = -62135596800; // 0001-01-01
            const long maxEpochSeconds = 253402300799;  // 9999-12-31
            if (resets.ValueKind == JsonValueKind.Number && resets.TryGetInt64(out var epochSeconds)
                && epochSeconds is >= minEpochSeconds and <= maxEpochSeconds)
            {
                resetsAt = DateTimeOffset.FromUnixTimeSeconds(epochSeconds);
            }
            else if (resets.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(resets.GetString(), out var parsed))
            {
                resetsAt = parsed;
            }
        }

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
