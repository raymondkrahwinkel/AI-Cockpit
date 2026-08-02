using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.ClaudeProvider.Tests;

// What a Claude session reports it is running out of, read from the JSON Claude Code hands its statusline
// command (AC-229). That blob is the only machine-readable source for the five-hour and weekly allowances — they
// arrive in response headers the cockpit never sees, and appear in no transcript and no CLI subcommand. The
// reading lives here rather than in the host because the shape is Claude's and has moved between versions.
public class ClaudeUsageSignalsTests
{
    private const string FullBlob = """
        {
          "session_id": "abc",
          "model": { "display_name": "Opus 4.8" },
          "context_window": { "used_percentage": 42.5, "context_window_size": 200000 },
          "rate_limits": {
            "five_hour": { "used_percentage": 18.2, "resets_at": "2026-07-14T22:00:00Z" },
            "seven_day": { "used_percentage": 7.4, "resets_at": "2026-07-20T00:00:00Z" }
          }
        }
        """;

    [Fact]
    public void TheStatusLineBlob_YieldsAllThreeReadings()
    {
        var readings = ClaudeUsageSignals.Read(FullBlob);

        Assert.Equal(3, System.Linq.Enumerable.Count(readings));
        Assert.Equal(42.5, _Percent(readings, ClaudeUsageSignals.ContextKey));
        Assert.Equal(18.2, _Percent(readings, ClaudeUsageSignals.FiveHourKey));
        Assert.Equal(7.4, _Percent(readings, ClaudeUsageSignals.WeeklyKey));
        Assert.Equal(DateTimeOffset.Parse("2026-07-14T22:00:00Z"), _Reading(readings, ClaudeUsageSignals.FiveHourKey).ResetsAt);
    }

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

    [Fact]
    public void AGarbledResetsAt_CostsTheMomentRatherThanTheReading()
    {
        // Outside year 1..9999 DateTimeOffset.FromUnixTimeSeconds throws. A crafted or corrupted snapshot must
        // still yield its percentage — losing the bar entirely because its reset time was nonsense is worse.
        var readings = ClaudeUsageSignals.Read("""
            { "rate_limits": { "five_hour": { "used_percentage": 55, "resets_at": 99999999999999 } } }
            """);

        var reading = _Reading(readings, ClaudeUsageSignals.FiveHourKey);
        Assert.Equal(55, reading.UsedPercent);
        Assert.Null(reading.ResetsAt);
    }

    [Fact]
    public void BeforeTheFirstResponse_NothingIsClaimed()
    {
        // Claude reports no rate_limits until it has spoken to the API, and none at all on a plan that has no
        // allowance. A reading of "0%" would be inventing a number, so there is simply no reading.
        var readings = ClaudeUsageSignals.Read("""{ "session_id": "abc", "model": { "display_name": "Opus 4.8" } }""");

        Assert.Empty(readings);
    }

    [Fact]
    public void OnAPlanWithoutAllowances_OnlyTheContextIsRead()
    {
        var readings = ClaudeUsageSignals.Read("""{ "context_window": { "used_percentage": 61.2 } }""");

        Assert.Single(readings);
        Assert.Equal(61.2, _Percent(readings, ClaudeUsageSignals.ContextKey));
    }

    [Fact]
    public void AFileCaughtMidWrite_IsNotAnError()
    {
        // The script writes whole and renames, but a truncated read is still possible on some filesystems — and a
        // status bar must never be the reason a session falls over.
        Assert.Empty(ClaudeUsageSignals.Read("""{ "context_window": { "used_per"""));
    }

    [Fact]
    public void TheAllowances_OfferAResume_AndTheContextDoesNot()
    {
        // A context window empties on a compaction, not at a moment, so there is nothing to schedule against.
        var declarations = ClaudeUsageSignals.Declarations;

        Assert.Equal(PluginUsageSignalKind.Fill, _Signal(declarations, ClaudeUsageSignals.ContextKey).Kind);
        Assert.False(_Signal(declarations, ClaudeUsageSignals.ContextKey).SupportsResume);
        Assert.Equal(PluginUsageSignalKind.Allowance, _Signal(declarations, ClaudeUsageSignals.FiveHourKey).Kind);
        Assert.True(_Signal(declarations, ClaudeUsageSignals.FiveHourKey).SupportsResume);
        Assert.True(_Signal(declarations, ClaudeUsageSignals.WeeklyKey).SupportsResume);
    }

    [Fact]
    public void EveryReading_NamesADeclaredSignal()
    {
        // A reading whose key matches no declaration is dropped by the host, so a typo here would silently cost a
        // bar rather than fail anywhere.
        var keys = ClaudeUsageSignals.Declarations.Select(signal => signal.Key).ToList();

        foreach (var signalKey in ClaudeUsageSignals.Read(FullBlob).Select(reading => reading.SignalKey))
        {
            Assert.Contains(signalKey, keys);
        }
    }

    private static PluginUsageReading _Reading(IReadOnlyList<PluginUsageReading> readings, string key) =>
        readings.Single(reading => reading.SignalKey == key);

    private static double _Percent(IReadOnlyList<PluginUsageReading> readings, string key) =>
        _Reading(readings, key).UsedPercent;

    private static PluginUsageSignal _Signal(IReadOnlyList<PluginUsageSignal> signals, string key) =>
        signals.Single(signal => signal.Key == key);
}
