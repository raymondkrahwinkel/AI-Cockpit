using System.Text.Json;

namespace Cockpit.Plugin.ClaudeProvider.Tests;

public class ClaudeSdkUsageTests
{
    private static ClaudeSdkUsage Observe(params string[] lines)
    {
        var usage = new ClaudeSdkUsage();
        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line);
            usage.Observe(document.RootElement);
        }

        return usage;
    }

    [Fact]
    public void AnAccountPastItsAllowance_ReportsTheOverageInsteadOfATidyHundred()
    {
        const string overage = """{"type":"rate_limit_event","rate_limit_info":{"status":"rejected","resetsAt":1785574800,"rateLimitType":"five_hour","utilization":1.37,"isUsingOverage":true},"uuid":"u","session_id":"s"}""";

        var status = Observe(overage).Status;

        Assert.NotNull(status);
        Assert.Equal(137d, Assert.Single(status.RateLimits).UsedPercent, precision: 10);
    }
}
