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
    // AC-481: total_cost_usd is what the session has cost so far, not what the last turn cost. Adding the
    // figures up billed every earlier turn again, so three turns of 1.00 → 2.00 → 3.00 read as 6.00.
    [Fact]
    public void Add_ReportedCostReplacesRatherThanAccumulates()
    {
        var meter = new SessionUsageMeter();

        meter.Add(new TokenUsage(10, 1, 0, 0), 1.00);
        meter.Add(new TokenUsage(10, 1, 0, 0), 2.00);

        // A costless turn between reports leaves the running cost alone.
        meter.Add(usage: null, costUsd: null);
        Assert.Equal(2.00, meter.TotalCostUsd, 9);

        meter.Add(new TokenUsage(10, 1, 0, 0), 3.00);

        Assert.Equal(3.00, meter.TotalCostUsd, 9);
        Assert.Equal(4, meter.Turns);
        Assert.Equal(33, meter.TotalTokens); // the usage-less turn counts as a turn but adds no tokens

        // AC-2405: those turns said nothing about a cache, so the rate is unknown rather than 0%.
        Assert.Null(meter.Totals.CacheHitRate);
        Assert.Contains("Cache hit: n/a (provider reports no cache)", meter.Tooltip);

        // Real usage: a Claude response (17428 of 73182 prompt tokens cached) and a normalised Codex turn (11008 of 19680).
        var claude = new TokenUsage(2, 767, 17428, 55752);
        var codex = new TokenUsage(8672, 130, 11008, 0) { CacheReported = true };
        Assert.Equal(17428d / 73182, claude.CacheHitRate);
        Assert.Equal(11008d / 19680, codex.CacheHitRate);

        // The session rate is weighted over the turns that reported; the earlier silent ones do not dilute it.
        meter.Add(claude, costUsd: null);
        meter.Add(codex, costUsd: null);

        Assert.Equal(28436d / 92862, meter.Totals.CacheHitRate);
        Assert.Contains("Cache hit: 31% session · 56% last turn", meter.Tooltip);
    }
}
