using Cockpit.App.ViewModels;
using Cockpit.Core.Sessions;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// The #8 token/cost meter's accumulation and formatting: a turn's own token usage folds into a running
/// session total while the reported cost — which already covers the whole session — replaces the previous
/// figure, a usage-less (error) turn contributes nothing, and the compact strings stay glanceable (k/M
/// tokens, sub-dollar precision, cost dropped when the provider reports none).
/// </summary>
public class SessionUsageMeterTests
{
    [Fact]
    public void FreshMeter_HasNoData()
    {
        var meter = new SessionUsageMeter();

        Assert.False(meter.HasData);
        Assert.Equal(0, meter.TotalTokens);
        Assert.Equal(0, meter.Turns);
    }

    [Fact]
    public void Add_SumsTokenBucketsAcrossTurns()
    {
        var meter = new SessionUsageMeter();

        meter.Add(new TokenUsage(100, 20, 300, 40), 0.01);
        meter.Add(new TokenUsage(10, 5, 30, 4), 0.02);

        Assert.Equal(110, meter.InputTokens);
        Assert.Equal(25, meter.OutputTokens);
        Assert.Equal(330, meter.CacheReadInputTokens);
        Assert.Equal(44, meter.CacheCreationInputTokens);
        Assert.Equal(509, meter.TotalTokens);
        Assert.Equal(2, meter.Turns);
        Assert.True(meter.HasData);
    }

    // AC-481: total_cost_usd is what the session has cost so far, not what the last turn cost. Adding the
    // figures up billed every earlier turn again, so three turns of 1.00 → 2.00 → 3.00 read as 6.00.
    [Fact]
    public void Add_ReportedCostReplacesRatherThanAccumulates()
    {
        var meter = new SessionUsageMeter();

        meter.Add(new TokenUsage(10, 1, 0, 0), 1.00);
        meter.Add(new TokenUsage(10, 1, 0, 0), 2.00);
        meter.Add(new TokenUsage(10, 1, 0, 0), 3.00);

        Assert.Equal(3.00, meter.TotalCostUsd, 9);
    }

    // The figures a real two-turn claude session reported (measured for AC-481): the second result carries
    // the session total, so the meter must land on it and not on the sum of the two.
    [Fact]
    public void Add_MeasuredTwoTurnSession_LandsOnTheLastReportedTotal()
    {
        var meter = new SessionUsageMeter();

        meter.Add(new TokenUsage(2, 3, 20_504, 6_850), 0.078837);
        meter.Add(new TokenUsage(2, 3, 27_354, 2_730), 0.119899);

        Assert.Equal(0.119899, meter.TotalCostUsd, 9);
    }

    // An error result carries the session total unchanged, so the same figure twice is one spend, not two.
    [Fact]
    public void Add_EqualConsecutiveReports_CountThatCostOnce()
    {
        var meter = new SessionUsageMeter();

        meter.Add(new TokenUsage(10, 1, 0, 0), 0.05);
        meter.Add(new TokenUsage(10, 1, 0, 0), 0.05);

        Assert.Equal(0.05, meter.TotalCostUsd, 9);
    }

    [Fact]
    public void Add_CostlessTurnBetweenReports_LeavesTheRunningCostAlone()
    {
        var meter = new SessionUsageMeter();

        meter.Add(new TokenUsage(10, 1, 0, 0), 1.00);
        meter.Add(usage: null, costUsd: null);
        meter.Add(new TokenUsage(10, 1, 0, 0), 2.00);

        Assert.Equal(2.00, meter.TotalCostUsd, 9);
        Assert.Equal(3, meter.Turns);
    }

    [Fact]
    public void Add_UsagelessTurn_CountsTurnButAddsNothing()
    {
        var meter = new SessionUsageMeter();
        meter.Add(new TokenUsage(100, 20, 0, 0), 0.01);

        meter.Add(usage: null, costUsd: null);

        Assert.Equal(120, meter.TotalTokens);
        Assert.Equal(0.01, meter.TotalCostUsd, 9);
        Assert.Equal(2, meter.Turns);
    }

    [Fact]
    public void HasData_TrueOnCostEvenWithoutTokens()
    {
        var meter = new SessionUsageMeter();

        meter.Add(usage: null, costUsd: 0.005);

        Assert.True(meter.HasData);
    }

    [Fact]
    public void Summary_IncludesCostWhenPresent()
    {
        var meter = new SessionUsageMeter();
        meter.Add(new TokenUsage(45_200, 0, 0, 0), 0.0123);

        Assert.Equal("45.2k tok · $0.0123", meter.Summary);
    }

    [Fact]
    public void Summary_DropsCostWhenProviderReportsNone()
    {
        var meter = new SessionUsageMeter();
        meter.Add(new TokenUsage(500, 0, 0, 0), costUsd: null);

        Assert.Equal("500 tok", meter.Summary);
    }

    [Theory]
    [InlineData(950, "950")]
    [InlineData(45_210, "45.2k")]
    [InlineData(2_300_000, "2.30M")]
    public void FormatTokens_IsGlanceable(int tokens, string expected)
        => Assert.Equal(expected, SessionUsageMeter.FormatTokens(tokens));

    [Theory]
    [InlineData(0.0123, "$0.0123")]
    [InlineData(2.5, "$2.50")]
    public void FormatCost_UsesExtraDigitsUnderADollar(double cost, string expected)
        => Assert.Equal(expected, SessionUsageMeter.FormatCost(cost));

    [Fact]
    public void Tooltip_BreaksDownBucketsAndTurnCount()
    {
        var meter = new SessionUsageMeter();
        meter.Add(new TokenUsage(10_000, 2_000, 30_000, 4_000), 0.05);

        Assert.Equal(
            "Input 10.0k · Output 2.0k · Cache read 30.0k · Cache write 4.0k · $0.0500 · 1 turn",
            meter.Tooltip);
    }
}
