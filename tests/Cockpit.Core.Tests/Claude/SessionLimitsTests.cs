using FluentAssertions;
using Cockpit.App.ViewModels;
using Cockpit.Core.Sessions;

namespace Cockpit.Core.Tests.Claude;

/// <summary>
/// The limits a TTY session shows in its header, read from the JSON Claude Code hands its statusline command.
/// That blob is the only machine-readable source for the five-hour and weekly allowances — they arrive in
/// response headers the cockpit never sees, and appear in no transcript and no CLI subcommand.
/// </summary>
public class SessionLimitsTests
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
    public void TheStatusLineBlob_YieldsAllThreeNumbers()
    {
        var limits = SessionLimits.TryParse(FullBlob);

        limits.Should().NotBeNull();
        limits!.ContextUsedPercent.Should().Be(42.5);
        limits.FiveHourUsedPercent.Should().Be(18.2);
        limits.SevenDayUsedPercent.Should().Be(7.4);
        limits.FiveHourResetsAt.Should().Be(DateTimeOffset.Parse("2026-07-14T22:00:00Z"));
    }

    [Fact]
    public void ResetsAt_AsAUnixEpochNumber_IsParsed()
    {
        // The real statusline (2.1.209) sends resets_at as a Unix-epoch-seconds number, not an ISO string — the
        // parser must read it, or the header shows the windows without their reset time (AC-37).
        const long fiveHourEpoch = 1784415000;   // 2026-07-16T18:30:00Z
        const long sevenDayEpoch = 1784970000;
        var limits = SessionLimits.TryParse($$"""
            {
              "context_window": { "used_percentage": 86 },
              "rate_limits": {
                "five_hour": { "used_percentage": 7, "resets_at": {{fiveHourEpoch}} },
                "seven_day": { "used_percentage": 18, "resets_at": {{sevenDayEpoch}} }
              }
            }
            """);

        limits.Should().NotBeNull();
        limits!.FiveHourResetsAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(fiveHourEpoch));
        limits.SevenDayResetsAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(sevenDayEpoch));
    }

    [Fact]
    public void BeforeTheFirstResponse_NothingIsClaimed()
    {
        // Claude reports no rate_limits until it has spoken to the API, and none at all on a plan that has no
        // allowance to report. A bar that filled that silence with "0%" would be inventing a number — so each bar
        // hides itself when its value is null, and there is nothing to describe.
        var limits = SessionLimits.TryParse("""{ "session_id": "abc", "model": { "display_name": "Opus 4.8" } }""");

        limits.Should().NotBeNull();
        limits!.HasAny.Should().BeFalse();
        TtyViewModel.DescribeLimits(limits).Should().BeEmpty();
    }

    [Fact]
    public void OnlyWhatWasReported_IsDescribed()
    {
        // The context percentage without the allowances: what a session on a plan with no rate limits looks like.
        var limits = SessionLimits.TryParse("""{ "context_window": { "used_percentage": 61.2 } }""");

        TtyViewModel.DescribeLimits(limits!).Should().Be("Context window: 61% used");
    }

    [Fact]
    public void TheHoverText_SaysWhatTheBarsCannot_WhenEachWindowRollsOver()
    {
        var description = TtyViewModel.DescribeLimits(SessionLimits.TryParse(FullBlob)!);

        description.Should().Contain("Context window: 43% used");
        description.Should().Contain("Session (5 hours): 18% used — resets");
        description.Should().Contain("Week: 7% used — resets");
    }

    [Fact]
    public void AFileCaughtMidWrite_IsNotAnError()
    {
        // The script writes whole and renames, but a truncated read is still possible on some filesystems — and a
        // status bar must never be the reason a session falls over.
        SessionLimits.TryParse("""{ "context_window": { "used_per""").Should().BeNull();
    }
}
