using System.Text.Json;

namespace Cockpit.Plugin.ClaudeProvider.Tests;

// The shape here is copied from a live `get_usage` control-response (CLI 2.1.226) rather than invented, because
// the trap this guards is a shape difference: `utilization` is a whole percentage in this reply and a fraction on
// `rate_limit_event`, and `resets_at` is an ISO string here where the event uses epoch seconds. A fixture built to
// the code's expectations would have hidden both.
public class ClaudeUsageWindowsTests
{
    private static JsonElement _Reply(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static JsonElement _Usage(double fiveHour = 7, double weekly = 1) =>
        _Reply($$"""
        {
          "session": { "total_cost_usd": 0, "model_usage": {} },
          "subscription_type": "max",
          "rate_limits_available": true,
          "rate_limits": {
            "five_hour": { "utilization": {{fiveHour}}, "resets_at": "2026-08-08T18:00:00.978410+00:00", "limit_dollars": null },
            "seven_day": { "utilization": {{weekly}}, "resets_at": "2026-08-15T09:00:00.978430+00:00", "limit_dollars": null },
            "seven_day_oauth_apps": null
          }
        }
        """);

    [Fact]
    public void AReply_YieldsBothWindowsWithTheirResetTimes()
    {
        var windows = ClaudeUsageWindows.Read(_Usage());

        Assert.Equal(["five_hour", "seven_day"], windows.Keys.OrderBy(key => key, StringComparer.Ordinal));
        Assert.Equal("5h", windows["five_hour"].Label);
        Assert.Equal("wk", windows["seven_day"].Label);
        Assert.Equal(DateTimeOffset.Parse("2026-08-08T18:00:00.978410+00:00"), windows["five_hour"].ResetsAt);
    }

    // The figure is taken as written. On `rate_limit_event` the same word is a fraction that gets multiplied
    // by 100, and scaling it here too would report an account at 7% as 700% full.
    [Fact]
    public void TheFigureIsAPercentageAlready_NotAFraction()
    {
        var windows = ClaudeUsageWindows.Read(_Usage());

        Assert.Equal(7d, windows["five_hour"].UsedPercent, precision: 10);
        Assert.Equal(1d, windows["seven_day"].UsedPercent, precision: 10);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"rate_limits":null}""")]
    [InlineData("""{"rate_limits":{}}""")]
    [InlineData("""{"rate_limits":{"five_hour":"soon"}}""")]
    [InlineData("[1,2,3]")]
    public void RubbishYieldsNothing_RatherThanThrowingOnThePoll(string json) =>
        Assert.Empty(ClaudeUsageWindows.Read(_Reply(json)));

    // A window the reply carries as null (seven_day_oauth_apps does) is absent, not zero.
    [Fact]
    public void AWindowWithoutAFigure_IsAbsentRatherThanZero()
    {
        var windows = ClaudeUsageWindows.Read(_Reply("""{"rate_limits":{"five_hour":null,"seven_day":{"utilization":7}}}"""));

        Assert.False(windows.ContainsKey("five_hour"));
        Assert.Equal(7d, windows["seven_day"].UsedPercent, precision: 10);
        Assert.Null(windows["seven_day"].ResetsAt);
    }

    [Fact]
    public void ANegativeFigure_IsRefused()
    {
        var windows = ClaudeUsageWindows.Read(_Usage(fiveHour: -1));

        Assert.False(windows.ContainsKey("five_hour"));
        Assert.True(windows.ContainsKey("seven_day"));
    }

    // Past the allowance is real and must survive — the operator most needs to see an overage.
    [Fact]
    public void PastTheAllowance_IsKept() =>
        Assert.Equal(137d, ClaudeUsageWindows.Read(_Usage(fiveHour: 137))["five_hour"].UsedPercent, precision: 10);
}
