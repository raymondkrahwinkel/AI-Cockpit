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

    [Fact]
    public void Add_CostlessTurnBetweenReports_LeavesTheRunningCostAlone()
    {
        var meter = new SessionUsageMeter();

        meter.Add(new TokenUsage(10, 1, 0, 0), 1.00);
        meter.Add(usage: null, costUsd: null);
        meter.Add(new TokenUsage(10, 1, 0, 0), 2.00);

        Assert.Equal(2.00, meter.TotalCostUsd, 9);
        Assert.Equal(3, meter.Turns);
        Assert.Equal(22, meter.TotalTokens); // the usage-less turn counts as a turn but adds no tokens
    }
}
