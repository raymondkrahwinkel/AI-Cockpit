using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.ClaudeProvider.Tests;

public class ClaudeModelIntelligenceTests
{
    public static IEnumerable<object[]> QuotaCases =>
    [
        [false],
        [true],
    ];

    [Theory]
    [MemberData(nameof(QuotaCases))]
    public void QuotaStateAndObservations_KeepUnknownDistinctFromALiveRateLimitReading(bool reportsRateLimit)
    {
        var observations = new List<PluginModelObservation>();
        var intelligence = new ClaudeModelIntelligence();
        var usage = new ClaudeSdkUsage(intelligence, observations.Add);

        if (reportsRateLimit)
        {
            using var document = JsonDocument.Parse("""
                {"type":"rate_limit_event","rate_limit_info":{"status":"rejected","rateLimitType":"five_hour","utilization":1,"resetsAt":1785574800}}
                """);
            usage.Observe(document.RootElement);
        }

        var quota = intelligence.Read().Quota;
        var asOf = quota.SingleOrDefault()?.Source.AsOf;

        if (!reportsRateLimit)
        {
            Assert.Empty(quota);
            Assert.Null(asOf);
            Assert.Empty(observations);
            return;
        }

        Assert.Equal(100d, Assert.Single(quota).Window.UsedPercent, precision: 10);
        Assert.NotNull(asOf);
        var refusal = Assert.Single(observations, observation => observation.Kind == PluginModelObservationKind.RateLimitRefusal);
        Assert.NotEqual(default, refusal.At);
    }
}
