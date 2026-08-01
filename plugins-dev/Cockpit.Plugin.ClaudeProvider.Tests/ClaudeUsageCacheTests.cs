namespace Cockpit.Plugin.ClaudeProvider.Tests;

/// <summary>
/// AC-549. The shape here is copied from a real <c>.claude.json</c> (CLI 2.1.220) rather than invented, because the
/// trap this guards is a shape difference: <c>utilization</c> is a whole percentage in this file and a fraction on
/// <c>rate_limit_event</c>, and <c>resets_at</c> is an ISO string here where the event uses epoch seconds. A fixture
/// built to the code's expectations would have hidden both.
/// </summary>
public class ClaudeUsageCacheTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-01T12:53:00Z");

    private static string _Snapshot(string fetchedAtIso, double fiveHour = 2, double weekly = 7) =>
        $$"""
        {
          "oauthAccount": { "organizationRateLimitTier": "default_claude_max_20x" },
          "cachedUsageUtilization": {
            "fetchedAtMs": {{DateTimeOffset.Parse(fetchedAtIso).ToUnixTimeMilliseconds()}},
            "accountUuid": "75f93855-a456-48c1-8415-e25f63fc51f0",
            "utilization": {
              "five_hour": { "utilization": {{fiveHour}}, "resets_at": "2026-08-01T17:40:00.572562+00:00", "limit_dollars": null },
              "seven_day": { "utilization": {{weekly}}, "resets_at": "2026-08-08T08:59:59.572583+00:00", "limit_dollars": null },
              "seven_day_oauth_apps": null
            }
          }
        }
        """;

    [Fact]
    public void AFreshSnapshot_YieldsBothWindowsWithTheirResetTimes()
    {
        var windows = ClaudeUsageCache.Read(_Snapshot("2026-08-01T12:52:23Z"), Now);

        Assert.Equal(["five_hour", "seven_day"], windows.Keys.OrderBy(key => key, StringComparer.Ordinal));
        Assert.Equal("5h", windows["five_hour"].Label);
        Assert.Equal("wk", windows["seven_day"].Label);
        Assert.Equal(DateTimeOffset.Parse("2026-08-01T17:40:00.572562+00:00"), windows["five_hour"].ResetsAt);
    }

    /// <summary>
    /// The figure is taken as written. On <c>rate_limit_event</c> the same word is a fraction that gets multiplied
    /// by 100, and scaling it here too would report an account at 2% as 200% full.
    /// </summary>
    [Fact]
    public void TheFigureIsAPercentageAlready_NotAFraction()
    {
        var windows = ClaudeUsageCache.Read(_Snapshot("2026-08-01T12:52:23Z"), Now);

        Assert.Equal(2d, windows["five_hour"].UsedPercent, precision: 10);
        Assert.Equal(7d, windows["seven_day"].UsedPercent, precision: 10);
    }

    /// <summary>
    /// Found in the wild at 68 hours stale, because nothing on the SDK route had ever refreshed it. Showing that as
    /// the current figure invents a number, and an allowance that reads emptier than it is is the worst direction
    /// to be wrong in.
    /// </summary>
    [Fact]
    public void AStaleSnapshot_YieldsNothingRatherThanAnOldNumber()
    {
        var stale = _Snapshot(Now.AddHours(-68).ToString("O"));

        Assert.Empty(ClaudeUsageCache.Read(stale, Now));
    }

    [Fact]
    public void TheBoundaryIsInclusive_AndOneTickPastItIsNot()
    {
        Assert.NotEmpty(ClaudeUsageCache.Read(_Snapshot(Now.Subtract(ClaudeUsageCache.MaxAge).ToString("O")), Now));
        Assert.Empty(ClaudeUsageCache.Read(_Snapshot(Now.Subtract(ClaudeUsageCache.MaxAge).AddSeconds(-1).ToString("O")), Now));
    }

    /// <summary>A clock that jumped backwards must not make a future snapshot look infinitely fresh.</summary>
    [Fact]
    public void ASnapshotStampedInTheFuture_IsNotTrusted()
    {
        Assert.Empty(ClaudeUsageCache.Read(_Snapshot(Now.AddMinutes(1).ToString("O")), Now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("{}")]
    [InlineData("""{"cachedUsageUtilization":{}}""")]
    [InlineData("""{"cachedUsageUtilization":{"fetchedAtMs":"soon","utilization":{}}}""")]
    public void RubbishYieldsNothing_RatherThanThrowingOnTheStdoutPump(string json) =>
        Assert.Empty(ClaudeUsageCache.Read(json, Now));

    /// <summary>A window the file carries as null (seven_day_oauth_apps does) is absent, not zero.</summary>
    [Fact]
    public void AWindowWithoutAFigure_IsAbsentRatherThanZero()
    {
        const string json = """
        {"cachedUsageUtilization":{"fetchedAtMs":0,"utilization":{"five_hour":null,"seven_day":{"utilization":7}}}}
        """;

        var windows = ClaudeUsageCache.Read(json.Replace("\"fetchedAtMs\":0", $"\"fetchedAtMs\":{Now.ToUnixTimeMilliseconds()}"), Now);

        Assert.False(windows.ContainsKey("five_hour"));
        Assert.Equal(7d, windows["seven_day"].UsedPercent, precision: 10);
        Assert.Null(windows["seven_day"].ResetsAt);
    }

    [Fact]
    public void ANegativeFigure_IsRefused()
    {
        var windows = ClaudeUsageCache.Read(_Snapshot("2026-08-01T12:52:23Z", fiveHour: -1), Now);

        Assert.False(windows.ContainsKey("five_hour"));
        Assert.True(windows.ContainsKey("seven_day"));
    }

    /// <summary>Past the allowance is real and must survive — the operator most needs to see an overage.</summary>
    [Fact]
    public void PastTheAllowance_IsKept()
    {
        var windows = ClaudeUsageCache.Read(_Snapshot("2026-08-01T12:52:23Z", fiveHour: 137), Now);

        Assert.Equal(137d, windows["five_hour"].UsedPercent, precision: 10);
    }

    /// <summary>
    /// The refresh interval has to stay comfortably inside the freshness limit, or every reading expires before the
    /// next refresh can replace it and the pill flickers empty between turns.
    /// </summary>
    [Fact]
    public void TheRefreshIntervalStaysWellInsideTheFreshnessLimit() =>
        Assert.True(ClaudeUsageRefresh.Interval * 2 <= ClaudeUsageCache.MaxAge,
            $"refresh every {ClaudeUsageRefresh.Interval} against a {ClaudeUsageCache.MaxAge} limit leaves no margin");
}
