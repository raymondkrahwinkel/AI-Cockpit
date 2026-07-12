using Cockpit.App.ViewModels;
using Cockpit.Core.Sessions;
using FluentAssertions;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// The #8 token/cost meter's accumulation and formatting: each turn's result usage/cost folds into a
/// running session total, a usage-less (error) turn contributes nothing, and the compact strings stay
/// glanceable (k/M tokens, sub-dollar precision, cost dropped when the provider reports none).
/// </summary>
public class SessionUsageMeterTests
{
    [Fact]
    public void FreshMeter_HasNoData()
    {
        var meter = new SessionUsageMeter();

        meter.HasData.Should().BeFalse();
        meter.TotalTokens.Should().Be(0);
        meter.Turns.Should().Be(0);
    }

    [Fact]
    public void Add_SumsUsageAndCostAcrossTurns()
    {
        var meter = new SessionUsageMeter();

        meter.Add(new TokenUsage(100, 20, 300, 40), 0.01);
        meter.Add(new TokenUsage(10, 5, 30, 4), 0.02);

        meter.InputTokens.Should().Be(110);
        meter.OutputTokens.Should().Be(25);
        meter.CacheReadInputTokens.Should().Be(330);
        meter.CacheCreationInputTokens.Should().Be(44);
        meter.TotalTokens.Should().Be(509);
        meter.TotalCostUsd.Should().BeApproximately(0.03, 1e-9);
        meter.Turns.Should().Be(2);
        meter.HasData.Should().BeTrue();
    }

    [Fact]
    public void Add_UsagelessTurn_CountsTurnButAddsNothing()
    {
        var meter = new SessionUsageMeter();
        meter.Add(new TokenUsage(100, 20, 0, 0), 0.01);

        meter.Add(usage: null, costUsd: null);

        meter.TotalTokens.Should().Be(120);
        meter.TotalCostUsd.Should().BeApproximately(0.01, 1e-9);
        meter.Turns.Should().Be(2);
    }

    [Fact]
    public void HasData_TrueOnCostEvenWithoutTokens()
    {
        var meter = new SessionUsageMeter();

        meter.Add(usage: null, costUsd: 0.005);

        meter.HasData.Should().BeTrue();
    }

    [Fact]
    public void Summary_IncludesCostWhenPresent()
    {
        var meter = new SessionUsageMeter();
        meter.Add(new TokenUsage(45_200, 0, 0, 0), 0.0123);

        meter.Summary.Should().Be("45.2k tok · $0.0123");
    }

    [Fact]
    public void Summary_DropsCostWhenProviderReportsNone()
    {
        var meter = new SessionUsageMeter();
        meter.Add(new TokenUsage(500, 0, 0, 0), costUsd: null);

        meter.Summary.Should().Be("500 tok");
    }

    [Theory]
    [InlineData(950, "950")]
    [InlineData(45_210, "45.2k")]
    [InlineData(2_300_000, "2.30M")]
    public void FormatTokens_IsGlanceable(int tokens, string expected)
        => SessionUsageMeter.FormatTokens(tokens).Should().Be(expected);

    [Theory]
    [InlineData(0.0123, "$0.0123")]
    [InlineData(2.5, "$2.50")]
    public void FormatCost_UsesExtraDigitsUnderADollar(double cost, string expected)
        => SessionUsageMeter.FormatCost(cost).Should().Be(expected);

    [Fact]
    public void Tooltip_BreaksDownBucketsAndTurnCount()
    {
        var meter = new SessionUsageMeter();
        meter.Add(new TokenUsage(10_000, 2_000, 30_000, 4_000), 0.05);

        meter.Tooltip.Should().Be(
            "Input 10.0k · Output 2.0k · Cache read 30.0k · Cache write 4.0k · $0.0500 · 1 turn");
    }
}
