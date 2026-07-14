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
    public void BeforeTheFirstResponse_NothingIsClaimed()
    {
        // Claude reports no rate_limits until it has spoken to the API, and none at all on a plan that has no
        // allowance to report. A header that filled that silence with "0%" would be inventing a number.
        var limits = SessionLimits.TryParse("""{ "session_id": "abc", "model": { "display_name": "Opus 4.8" } }""");

        limits.Should().NotBeNull();
        limits!.HasAny.Should().BeFalse();
        ClaudeTtyViewModel.FormatLimits(limits).Should().BeEmpty();
    }

    [Fact]
    public void OnlyWhatWasReported_IsShown()
    {
        // The context percentage without the allowances: what a session on a plan with no rate limits looks like.
        var limits = SessionLimits.TryParse("""{ "context_window": { "used_percentage": 61.2 } }""");

        ClaudeTtyViewModel.FormatLimits(limits!).Should().Be("ctx 61%");
    }

    [Fact]
    public void TheHeader_ReadsAsOneLine()
    {
        ClaudeTtyViewModel.FormatLimits(SessionLimits.TryParse(FullBlob)!).Should().Be("ctx 43% · 5h 18% · wk 7%");
    }

    [Fact]
    public void AFileCaughtMidWrite_IsNotAnError()
    {
        // The script writes whole and renames, but a truncated read is still possible on some filesystems — and a
        // status bar must never be the reason a session falls over.
        SessionLimits.TryParse("""{ "context_window": { "used_per""").Should().BeNull();
    }
}
