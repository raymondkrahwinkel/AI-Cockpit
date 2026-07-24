using Cockpit.Plugins.Abstractions.Sessions;
using FluentAssertions;

namespace Cockpit.Plugin.ClaudeProvider.Tests;

/// <summary>
/// What a Claude session reports it is running out of, read from the JSON Claude Code hands its statusline
/// command (AC-229). That blob is the only machine-readable source for the five-hour and weekly allowances — they
/// arrive in response headers the cockpit never sees, and appear in no transcript and no CLI subcommand. The
/// reading lives here rather than in the host because the shape is Claude's and has moved between versions.
/// </summary>
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

        readings.Should().HaveCount(3);
        _Percent(readings, ClaudeUsageSignals.ContextKey).Should().Be(42.5);
        _Percent(readings, ClaudeUsageSignals.FiveHourKey).Should().Be(18.2);
        _Percent(readings, ClaudeUsageSignals.WeeklyKey).Should().Be(7.4);
        _Reading(readings, ClaudeUsageSignals.FiveHourKey).ResetsAt.Should().Be(DateTimeOffset.Parse("2026-07-14T22:00:00Z"));
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

        _Reading(readings, ClaudeUsageSignals.FiveHourKey).ResetsAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(fiveHourEpoch));
        _Reading(readings, ClaudeUsageSignals.WeeklyKey).ResetsAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(sevenDayEpoch));
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
        reading.UsedPercent.Should().Be(55);
        reading.ResetsAt.Should().BeNull();
    }

    [Fact]
    public void BeforeTheFirstResponse_NothingIsClaimed()
    {
        // Claude reports no rate_limits until it has spoken to the API, and none at all on a plan that has no
        // allowance. A reading of "0%" would be inventing a number, so there is simply no reading.
        var readings = ClaudeUsageSignals.Read("""{ "session_id": "abc", "model": { "display_name": "Opus 4.8" } }""");

        readings.Should().BeEmpty();
    }

    [Fact]
    public void OnAPlanWithoutAllowances_OnlyTheContextIsRead()
    {
        var readings = ClaudeUsageSignals.Read("""{ "context_window": { "used_percentage": 61.2 } }""");

        readings.Should().ContainSingle();
        _Percent(readings, ClaudeUsageSignals.ContextKey).Should().Be(61.2);
    }

    [Fact]
    public void AFileCaughtMidWrite_IsNotAnError()
    {
        // The script writes whole and renames, but a truncated read is still possible on some filesystems — and a
        // status bar must never be the reason a session falls over.
        ClaudeUsageSignals.Read("""{ "context_window": { "used_per""").Should().BeEmpty();
    }

    [Fact]
    public void TheAllowances_OfferAResume_AndTheContextDoesNot()
    {
        // A context window empties on a compaction, not at a moment, so there is nothing to schedule against.
        var declarations = ClaudeUsageSignals.Declarations;

        _Signal(declarations, ClaudeUsageSignals.ContextKey).Kind.Should().Be(PluginUsageSignalKind.Fill);
        _Signal(declarations, ClaudeUsageSignals.ContextKey).SupportsResume.Should().BeFalse();
        _Signal(declarations, ClaudeUsageSignals.FiveHourKey).Kind.Should().Be(PluginUsageSignalKind.Allowance);
        _Signal(declarations, ClaudeUsageSignals.FiveHourKey).SupportsResume.Should().BeTrue();
        _Signal(declarations, ClaudeUsageSignals.WeeklyKey).SupportsResume.Should().BeTrue();
    }

    [Fact]
    public void EveryReading_NamesADeclaredSignal()
    {
        // A reading whose key matches no declaration is dropped by the host, so a typo here would silently cost a
        // bar rather than fail anywhere.
        var keys = ClaudeUsageSignals.Declarations.Select(signal => signal.Key);

        ClaudeUsageSignals.Read(FullBlob).Select(reading => reading.SignalKey).Should().BeSubsetOf(keys);
    }

    private static PluginUsageReading _Reading(IReadOnlyList<PluginUsageReading> readings, string key) =>
        readings.Single(reading => reading.SignalKey == key);

    private static double _Percent(IReadOnlyList<PluginUsageReading> readings, string key) =>
        _Reading(readings, key).UsedPercent;

    private static PluginUsageSignal _Signal(IReadOnlyList<PluginUsageSignal> signals, string key) =>
        signals.Single(signal => signal.Key == key);
}
