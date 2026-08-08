using System.Text.Json;

namespace Cockpit.Plugin.ClaudeProvider.Tests;

// `ClaudeSdkUsage` — the SDK route's source for the header's usage pill (AC-530).
//
// Neither fixture is hand-written. `RealTurn` is a verbatim capture from CLI 2.1.220 driven with the exact flags
// `ClaudeSdkArguments` builds (persistent `--input-format stream-json`, no `-p`), over a prompt that forced four
// sequential tool calls; only the message `content` arrays were emptied. `RealContextUsage` is a live
// `get_context_usage` control-response from 2.1.226. So a test passing here agrees with the real CLI rather than
// with a fixture written to match the code.
//
// What the turn's own token counts are *not* used for is now itself asserted: the context percentage comes from
// the CLI's `get_context_usage`, not from arithmetic over the stream.
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

    // Feeds a `get_context_usage` control-response, the way the driver's usage poll does.
    private static ClaudeSdkUsage Context(ClaudeSdkUsage usage, string json)
    {
        using var document = JsonDocument.Parse(json);
        usage.ObserveContextUsage(document.RootElement);
        return usage;
    }

    // Verbatim from a live 2.1.226 reply (categories trimmed — this class reads only `percentage`).
    private const string RealContextUsage = """
    {"categories":[{"name":"System prompt","tokens":2604},{"name":"Messages","tokens":6111}],
     "totalTokens":28981,"maxTokens":1000000,"rawMaxTokens":1000000,"autocompactSource":"auto","percentage":3}
    """;

    [Fact]
    public void TheContextPercentIsTheCliesOwnFigure_NotOneComputedHere()
    {
        // The CLI states the percentage `/context` prints. This used to be recomputed from the last assistant
        // line's token counts over the result line's window size, which broke when the assistant line said
        // `claude-opus-5` and the result line keyed its modelUsage `claude-opus-5[1m]`.
        var status = Context(new ClaudeSdkUsage(), RealContextUsage).Status;

        Assert.NotNull(status);
        Assert.Equal(3d, status.ContextUsedPercent);
    }

    [Fact]
    public void TheTokenCountsOnTheStreamAreNotReadAsAContextFigure()
    {
        // A whole real turn — four assistant lines and a result line stating a window — says nothing about the
        // context on its own any more. Only the rate-limit line in it produces anything.
        var status = Observe(RealTurn).Status;

        Assert.NotNull(status);
        Assert.Null(status.ContextUsedPercent);
        Assert.Single(status.RateLimits);
    }

    [Fact]
    public void ARealTurn_CarriesTheRateLimitWindowUnderItsHeaderLabel()
    {
        var status = Observe(RealTurn).Status;

        Assert.NotNull(status);
        var window = Assert.Single(status.RateLimits);

        // The wire says seven_day; the header says wk, exactly as the statusline route spells it.
        Assert.Equal("wk", window.Label);
        // utilization 0.98 is the same field the statusline multiplies by 100 for its own used_percentage.
        Assert.Equal(98d, window.UsedPercent, precision: 10);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1785574800), window.ResetsAt);
    }

    [Fact]
    public void BeforeTheProviderReportsAnything_ThereIsNoStatusAtAll()
    {
        // A header with nothing to say hides the pill rather than drawing an empty or zeroed one.
        Assert.Null(new ClaudeSdkUsage().Status);
        Assert.Null(Observe("""{"type":"system","subtype":"init","session_id":"s","model":"claude-opus-5"}""").Status);
    }

    [Theory]
    // A shape this build does not recognise must not fold in as a zero — "0%" reads as "nothing spent", which is
    // the one wrong answer worse than no answer. An errored or refused poll lands here too.
    [InlineData("{}")]
    [InlineData("""{"percentage":null}""")]
    [InlineData("""{"percentage":"3"}""")]
    [InlineData("[1,2,3]")]
    public void AContextReplyWithoutAUsableFigure_LeavesTheLastGoodReadingAlone(string json)
    {
        var usage = Context(new ClaudeSdkUsage(), RealContextUsage);

        Assert.Equal(3d, Context(usage, json).Status!.ContextUsedPercent);
    }

    [Fact]
    public void AContextReplyBeforeAnyOther_ProducesNoFigureRatherThanAZero() =>
        Assert.Null(Context(new ClaudeSdkUsage(), "{}").Status);

    [Fact]
    public void AWindowThisBuildHasNoDeclarationFor_IsCarriedUnderItsWireNameRatherThanDropped()
    {
        // A new Claude allowance must reach the header without a contract change; the host imposes no vocabulary.
        const string monthly = """{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","resetsAt":1785574800,"rateLimitType":"thirty_day","utilization":0.25},"uuid":"u","session_id":"s"}""";
        // A second unknown one, pushed in after it: two windows of equal rank still come out in a fixed order rather
        // than whatever order the lines happened to arrive in, so the pill does not reshuffle between turns.
        const string annual = """{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","rateLimitType":"annual","utilization":0.1},"uuid":"u","session_id":"s"}""";

        var status = Observe([.. RealTurn, monthly, annual]).Status;

        Assert.NotNull(status);
        Assert.Equal(["wk", "annual", "thirty_day"], status.RateLimits.Select(window => window.Label));
        Assert.Equal(25d, status.RateLimits[2].UsedPercent, precision: 10);
        // No resetsAt on the wire is a window without a reset, not a window at the epoch.
        Assert.Null(status.RateLimits[1].ResetsAt);
    }

    [Fact]
    public void TheFiveHourWindowIsOrderedAheadOfTheWeeklyOne_HoweverTheLinesArrived()
    {
        // The weekly line arrives first in RealTurn; the pill still reads 5h before wk.
        const string fiveHour = """{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","resetsAt":1785560000,"rateLimitType":"five_hour","utilization":0.52},"uuid":"u","session_id":"s"}""";

        var status = Observe([.. RealTurn, fiveHour]).Status;

        Assert.NotNull(status);
        Assert.Equal(["5h", "wk"], status.RateLimits.Select(window => window.Label));
    }

    [Fact]
    public void AnAccountPastItsAllowance_ReportsTheOverageInsteadOfATidyHundred()
    {
        const string overage = """{"type":"rate_limit_event","rate_limit_info":{"status":"rejected","resetsAt":1785574800,"rateLimitType":"five_hour","utilization":1.37,"isUsingOverage":true},"uuid":"u","session_id":"s"}""";

        var status = Observe(overage).Status;

        Assert.NotNull(status);
        Assert.Equal(137d, Assert.Single(status.RateLimits).UsedPercent, precision: 10);
    }

    [Theory]
    // The CLI already rounds; this only refuses what is not a percentage at all. Beyond the window (an over-long
    // prompt the CLI still counted) clamps to 100 rather than reading 104%.
    [InlineData("3", 3d)]
    [InlineData("0", 0d)]
    [InlineData("104", 100d)]
    [InlineData("-1", 0d)]
    public void AContextFigureIsClampedIntoTheRangeAPillCanDraw(string percentage, double expected) =>
        Assert.Equal(expected, Context(new ClaudeSdkUsage(), $$"""{"percentage":{{percentage}}}""").Status!.ContextUsedPercent);

    [Fact]
    public void ALineThatIsNotJsonThisClassUnderstands_IsIgnoredRatherThanThrowing()
    {
        var usage = new ClaudeSdkUsage();
        foreach (var line in (string[])["[1,2,3]", """ "text" """, "{}", """{"type":42}""", """{"type":"rate_limit_event"}""", """{"type":"rate_limit_event","rate_limit_info":{"rateLimitType":"five_hour"}}"""])
        {
            using var document = JsonDocument.Parse(line);
            usage.Observe(document.RootElement);
        }

        Assert.Null(usage.Status);
    }

    [Fact]
    public void TheWindowVocabularyIsTheOneTheStatuslineRouteAlreadyPublishes()
    {
        // One vocabulary, not two: if a declaration's label is ever renamed, this is what notices.
        Assert.Equal("5h", ClaudeUsageSignals.WindowLabel(ClaudeUsageSignals.FiveHourWireType));
        Assert.Equal("wk", ClaudeUsageSignals.WindowLabel(ClaudeUsageSignals.WeeklyWireType));
        Assert.Equal(
            ClaudeUsageSignals.Declarations.Single(signal => signal.Key == ClaudeUsageSignals.FiveHourKey).Label,
            ClaudeUsageSignals.WindowLabel(ClaudeUsageSignals.FiveHourWireType));
        Assert.Equal(
            ClaudeUsageSignals.Declarations.Single(signal => signal.Key == ClaudeUsageSignals.WeeklyKey).Label,
            ClaudeUsageSignals.WindowLabel(ClaudeUsageSignals.WeeklyWireType));
    }

    [Theory]
    // Beyond what DateTimeOffset.FromUnixTimeSeconds accepts. Found by driving the class with hostile input rather
    // than by review: this used to throw, and Observe runs on the stdout pump, whose catch-all turns any throw into a
    // PluginSessionError and completes the event stream — one unreadable timestamp would have ended the session.
    [InlineData("99999999999999")]
    [InlineData("-99999999999999")]
    [InlineData("9223372036854775807")]
    public void AResetTimestampOutsideTheRepresentableRange_CostsTheTimestampAndNotTheSession(string epochSeconds)
    {
        var line = $$"""{"type":"rate_limit_event","rate_limit_info":{"rateLimitType":"five_hour","utilization":0.5,"resetsAt":{{epochSeconds}}},"uuid":"u","session_id":"s"}""";

        var status = Observe(line).Status;

        Assert.NotNull(status);
        var window = Assert.Single(status.RateLimits);
        Assert.Equal(50d, window.UsedPercent, precision: 10);
        Assert.Null(window.ResetsAt);
    }

    [Fact]
    public void TheAcceptedResetRangeIsExactlyWhatTheFrameworkAccepts()
    {
        // Asserted against the framework itself rather than against a copy of the numbers, so the guard cannot drift
        // wider than the call it protects.
        var usage = new ClaudeSdkUsage();
        foreach (var boundary in new[] { DateTimeOffset.MinValue.ToUnixTimeSeconds(), DateTimeOffset.MaxValue.ToUnixTimeSeconds() })
        {
            var line = $$"""{"type":"rate_limit_event","rate_limit_info":{"rateLimitType":"five_hour","utilization":0.5,"resetsAt":{{boundary}}},"uuid":"u","session_id":"s"}""";
            using var document = JsonDocument.Parse(line);
            usage.Observe(document.RootElement);
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(boundary), Assert.Single(usage.Status!.RateLimits).ResetsAt);
        }
    }

    [Theory]
    // Overage above 100 is real and must survive; these are not overage, they are numbers that are not percentages.
    [InlineData("-0.5")]
    [InlineData("1e308")]
    [InlineData("-1e308")]
    public void AUtilisationThatIsNotAPercentageAtAll_ProducesNoWindow(string utilization)
    {
        var line = $$"""{"type":"rate_limit_event","rate_limit_info":{"rateLimitType":"five_hour","utilization":{{utilization}}},"uuid":"u","session_id":"s"}""";

        // 1e308 * 100 overflows to infinity, which would have reached the header as "∞%" and sized a bar against it.
        Assert.Null(Observe(line).Status);
    }


    // The real capture, exposed for the driver-level test that pushes it down an actual stdout pump.
    internal static IReadOnlyList<string> RealTurnLines => RealTurn;
}
