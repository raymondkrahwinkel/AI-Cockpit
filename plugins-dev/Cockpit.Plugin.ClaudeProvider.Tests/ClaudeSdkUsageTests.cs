using System.Text.Json;

namespace Cockpit.Plugin.ClaudeProvider.Tests;

public class ClaudeSdkUsageTests
{
    // Four assistant lines (one per API call), a rate-limit line, and the closing result line.
    private static readonly string[] RealTurn =
    [
        """{"type":"assistant","message":{"id":"msg_011CdaSJFjxTkfRUbCRyVZQi","type":"message","role":"assistant","model":"claude-opus-5","content":[],"usage":{"input_tokens":2,"cache_creation_input_tokens":11703,"cache_read_input_tokens":15185,"cache_creation":{"ephemeral_5m_input_tokens":0,"ephemeral_1h_input_tokens":11703},"output_tokens":70,"service_tier":"standard","inference_geo":"not_available"}},"session_id":"65246f03-83f9-4251-8c14-9374300f83b6","uuid":"ca4c3ece-eeb8-4514-a32b-4fb11dbe663d"}""",
        """{"type":"rate_limit_event","rate_limit_info":{"status":"allowed_warning","resetsAt":1785574800,"rateLimitType":"seven_day","utilization":0.98,"isUsingOverage":false,"surpassedThreshold":0.75},"uuid":"1bb7f36a-a75d-4af1-bac0-1a31e465d4ca","session_id":"65246f03-83f9-4251-8c14-9374300f83b6"}""",
        """{"type":"assistant","message":{"id":"msg_011CdaSJU48W49sweNKeT7bz","type":"message","role":"assistant","model":"claude-opus-5","content":[],"usage":{"input_tokens":2,"cache_creation_input_tokens":79,"cache_read_input_tokens":26888,"cache_creation":{"ephemeral_5m_input_tokens":0,"ephemeral_1h_input_tokens":79},"output_tokens":73,"service_tier":"standard","inference_geo":"not_available"}},"session_id":"65246f03-83f9-4251-8c14-9374300f83b6","uuid":"750a3ef0-2a28-455b-b823-23db3fa94414"}""",
        """{"type":"assistant","message":{"id":"msg_011CdaSJc7Jvighvq5SYodwW","type":"message","role":"assistant","model":"claude-opus-5","content":[],"usage":{"input_tokens":2,"cache_creation_input_tokens":79,"cache_read_input_tokens":26967,"cache_creation":{"ephemeral_5m_input_tokens":0,"ephemeral_1h_input_tokens":79},"output_tokens":68,"service_tier":"standard","inference_geo":"not_available"}},"session_id":"65246f03-83f9-4251-8c14-9374300f83b6","uuid":"a8ef50e1-3fab-44bf-9f40-f648a5c8a2b4"}""",
        """{"type":"assistant","message":{"id":"msg_011CdaSJmjkMUomNbnmfzY5R","type":"message","role":"assistant","model":"claude-opus-5","content":[],"usage":{"input_tokens":2,"cache_creation_input_tokens":79,"cache_read_input_tokens":27046,"cache_creation":{"ephemeral_5m_input_tokens":0,"ephemeral_1h_input_tokens":79},"output_tokens":1,"service_tier":"standard","inference_geo":"not_available"}},"session_id":"65246f03-83f9-4251-8c14-9374300f83b6","uuid":"e1e15e17-c851-4ada-9b40-e2528c17c84a"}""",
        """{"type":"result","subtype":"success","session_id":"65246f03-83f9-4251-8c14-9374300f83b6","usage":{"input_tokens":8,"cache_creation_input_tokens":11940,"cache_read_input_tokens":96086,"output_tokens":224},"modelUsage":{"claude-opus-5":{"inputTokens":8,"outputTokens":224,"cacheReadInputTokens":96086,"cacheCreationInputTokens":11940,"webSearchRequests":0,"costUSD":0.173083,"contextWindow":1000000,"maxOutputTokens":64000,"canonicalModel":"claude-opus-5","provider":"firstParty"}},"total_cost_usd":0.173083,"num_turns":4,"is_error":false}""",
    ];

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


    // The real capture, exposed for the driver-level test that pushes it down an actual stdout pump.
    internal static IReadOnlyList<string> RealTurnLines => RealTurn;
}
