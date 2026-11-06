using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.ClaudeProvider.Tests;

// What a Claude session reports it is running out of, read from the JSON Claude Code hands its statusline
// command (AC-229). That blob is the only machine-readable source for the five-hour and weekly allowances — they
// arrive in response headers the cockpit never sees, and appear in no transcript and no CLI subcommand. The
// reading lives here rather than in the host because the shape is Claude's and has moved between versions.
public class ClaudeUsageSignalsTests
{
    [Fact]
    public void ResetsAt_AsAUnixEpochNumber_IsRead()
    {
        // The real statusline (2.1.209) sends resets_at as a Unix-epoch-seconds number, not an ISO string — the
        // reader must take it, or a resume has no moment to schedule against (AC-231).
        const long fiveHourEpoch = 1784415000;   // 2026-07-16T18:30:00Z
        const long sevenDayEpoch = 1784970000;

        var readings = ClaudeUsageSignals.Read($$"""
            {
              "context_window": { "used_percentage": 86 },
              "rate_limits": {
                "five_hour": { "used_percentage": 7, "resets_at": {{fiveHourEpoch}} },
                "seven_day": { "used_percentage": 18, "resets_at": {{sevenDayEpoch}} }
              }
            }
            """);

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(fiveHourEpoch), _Reading(readings, ClaudeUsageSignals.FiveHourKey).ResetsAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(sevenDayEpoch), _Reading(readings, ClaudeUsageSignals.WeeklyKey).ResetsAt);
    }

    private static PluginUsageReading _Reading(IReadOnlyList<PluginUsageReading> readings, string key) =>
        readings.Single(reading => reading.SignalKey == key);
}
