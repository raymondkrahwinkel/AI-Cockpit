using System.Text.Json;

namespace Cockpit.Plugin.ClaudeProvider.Tests;

// `ClaudeSdkUsage` — the SDK route's source for the header's usage pill (AC-530).
//
// `RealTurn` is not a hand-written shape: it is a verbatim capture from CLI 2.1.220 driven with the
// exact flags `ClaudeSdkArguments` builds (persistent `--input-format stream-json`, no `-p`),
// over a prompt that forced four sequential tool calls so the turn really does contain more than one API response.
// Only the message `content` arrays were emptied, since this class never reads them. Every figure asserted
// below was measured off that run, so a test passing here is a test agreeing with the real CLI rather than with a
// fixture written to match the code.
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
    public void ARealTurn_ReportsTheContextWindowFromTheLastApiCall_NotTheTurnsTotal()
    {
        var status = Observe(RealTurn).Status;

        Assert.NotNull(status);

        // Measured: the last API call sent 2 + 79 + 27046 = 27127 tokens into a 1,000,000-token window => 3%.
        // The turn's own total (result.usage) is 8 + 11940 + 96086 = 108034 => 11%, because it counts every one of
        // the four calls. Reading that one would overstate a nearly-empty window by more than 3x; this assertion is
        // what keeps the two apart.
        Assert.Equal(3d, status.ContextUsedPercent);
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

    [Fact]
    public void WithoutAResultLine_TheContextPercentStaysUnknownRatherThanGuessingAWindowSize()
    {
        // Mid-turn there is a token count but no stated window size. Inventing a default (200k, say) would put a
        // confident wrong number in the header on every model whose window is not that.
        var status = Observe(RealTurn[0], RealTurn[1]).Status;

        Assert.NotNull(status);
        Assert.Null(status.ContextUsedPercent);
        Assert.Single(status.RateLimits);
    }

    [Fact]
    public void ASubAgentsApiCall_DoesNotBecomeTheSessionsContextReading()
    {
        // A Task sub-agent's assistant line carries parent_tool_use_id and its own, far smaller context. Folding it
        // in would make the main conversation look like it had just been emptied.
        const string subAgent = """{"type":"assistant","parent_tool_use_id":"toolu_01ABC","message":{"id":"msg_sub","type":"message","role":"assistant","model":"claude-opus-5","content":[],"usage":{"input_tokens":1,"cache_creation_input_tokens":0,"cache_read_input_tokens":120,"output_tokens":5}},"session_id":"65246f03-83f9-4251-8c14-9374300f83b6","uuid":"11111111-1111-1111-1111-111111111111"}""";

        var withSubAgent = Observe([.. RealTurn, subAgent]).Status;

        Assert.NotNull(withSubAgent);
        Assert.Equal(3d, withSubAgent.ContextUsedPercent);
    }

    [Fact]
    public void AUsageBlockNamingNoTokenFieldAtAll_LeavesTheLastGoodReadingAlone()
    {
        // A shape this build does not recognise must not fold in as a zero — "0%" reads as "nothing spent", which is
        // the one wrong answer worse than no answer.
        const string emptyUsage = """{"type":"assistant","message":{"id":"msg_x","type":"message","role":"assistant","model":"claude-opus-5","content":[],"usage":{"service_tier":"standard"}},"session_id":"s","uuid":"22222222-2222-2222-2222-222222222222"}""";

        var status = Observe([.. RealTurn, emptyUsage]).Status;

        Assert.NotNull(status);
        Assert.Equal(3d, status.ContextUsedPercent);
    }

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
    // The CLI rounds with JavaScript's Math.round — half away from zero. .NET's default is banker's rounding, which
    // would answer 2 for the first of these and disagree with the TTY route on exactly the halves.
    [InlineData(25_000, 1_000_000, 3d)]
    [InlineData(35_000, 1_000_000, 4d)]
    [InlineData(0, 1_000_000, 0d)]
    // Beyond the window (an over-long prompt the CLI still counted) clamps to 100 rather than reading 104%.
    [InlineData(1_040_000, 1_000_000, 100d)]
    public void TheContextPercentUsesTheCliesOwnRoundingAndClamp(long tokens, long window, double expected)
    {
        var assistant = $$$"""{"type":"assistant","message":{"id":"m","type":"message","role":"assistant","model":"claude-opus-5","content":[],"usage":{"input_tokens":{{{tokens}}},"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":1}},"session_id":"s","uuid":"u"}""";
        var result = $$$"""{"type":"result","subtype":"success","session_id":"s","usage":{"input_tokens":0},"modelUsage":{"claude-opus-5":{"contextWindow":{{{window}}}}},"is_error":false}""";

        var status = Observe(assistant, result).Status;

        Assert.NotNull(status);
        Assert.Equal(expected, status.ContextUsedPercent);
    }

    [Fact]
    public void AModelUsageEntryWithNoUsableWindowSize_LeavesTheContextPercentUnknown()
    {
        const string assistant = """{"type":"assistant","message":{"id":"m","type":"message","role":"assistant","model":"claude-opus-5","content":[],"usage":{"input_tokens":100,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":1}},"session_id":"s","uuid":"u"}""";

        // A zero window would be a division by zero; an absent one is simply not reported.
        Assert.Null(Observe(assistant, """{"type":"result","subtype":"success","session_id":"s","modelUsage":{"claude-opus-5":{"contextWindow":0}},"is_error":false}""").Status);
        Assert.Null(Observe(assistant, """{"type":"result","subtype":"success","session_id":"s","modelUsage":{"claude-opus-5":{"maxOutputTokens":64000}},"is_error":false}""").Status);
        Assert.Null(Observe(assistant, """{"type":"result","subtype":"success","session_id":"s","is_error":false}""").Status);
    }

    [Fact]
    public void AResultNamingSeveralModels_TakesTheWindowOfTheOneThatAnswered()
    {
        // Two models in one turn have different windows; picking the wrong entry silently reports the wrong fill.
        const string assistant = """{"type":"assistant","message":{"id":"m","type":"message","role":"assistant","model":"claude-haiku-4-5","content":[],"usage":{"input_tokens":100000,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":1}},"session_id":"s","uuid":"u"}""";
        const string result = """{"type":"result","subtype":"success","session_id":"s","modelUsage":{"claude-opus-5":{"contextWindow":1000000},"claude-haiku-4-5":{"contextWindow":200000}},"is_error":false}""";

        var status = Observe(assistant, result).Status;

        Assert.NotNull(status);
        // 100000/200000 = 50%, not 100000/1000000 = 10%.
        Assert.Equal(50d, status.ContextUsedPercent);
    }

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

    [Fact]
    public void NegativeTokenCounts_AreReadAsNotReportedRatherThanAsAnEmptyWindow()
    {
        const string result = """{"type":"result","subtype":"success","session_id":"s","modelUsage":{"claude-opus-5":{"contextWindow":200000}},"is_error":false}""";

        // Every component unusable means nothing was reported at all: summing them to a clamped zero would render a
        // confident "0% used" off figures that were never percentages.
        const string allNegative = """{"type":"assistant","message":{"id":"m","type":"message","role":"assistant","model":"claude-opus-5","content":[],"usage":{"input_tokens":-5000,"cache_creation_input_tokens":-1,"cache_read_input_tokens":-2,"output_tokens":1}},"session_id":"s","uuid":"u"}""";
        Assert.Null(Observe(allNegative, result).Status);

        // One bad component among genuine ones is dropped rather than subtracted: 0 + 0 + 50000 of 200000 is 25%,
        // where folding the -5000 in would have understated a quarter-full window as 22%.
        const string oneNegative = """{"type":"assistant","message":{"id":"m","type":"message","role":"assistant","model":"claude-opus-5","content":[],"usage":{"input_tokens":-5000,"cache_creation_input_tokens":0,"cache_read_input_tokens":50000,"output_tokens":1}},"session_id":"s","uuid":"u"}""";
        var status = Observe(oneNegative, result).Status;
        Assert.NotNull(status);
        Assert.Equal(25d, status.ContextUsedPercent);
    }

    [Fact]
    public void ATurnWhoseModelsCannotBeMatched_DropsTheWindowInsteadOfReusingTheLastOnesDenominator()
    {
        // Turn 1 settles a 200,000-token window. Turn 2 answers on a model the result line does not name — the one
        // shape where a stale window would still be sitting there, and 100,000 tokens against it reads as a tidy,
        // entirely believable 50% that belongs to neither turn. No window means no figure.
        const string firstAssistant = """{"type":"assistant","message":{"id":"m1","type":"message","role":"assistant","model":"claude-haiku-4-5","content":[],"usage":{"input_tokens":50000,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":1}},"session_id":"s","uuid":"u1"}""";
        const string firstResult = """{"type":"result","subtype":"success","session_id":"s","modelUsage":{"claude-haiku-4-5":{"contextWindow":200000}},"is_error":false}""";

        var settled = Observe(firstAssistant, firstResult).Status;
        Assert.NotNull(settled);
        Assert.Equal(25d, settled.ContextUsedPercent);

        const string secondAssistant = """{"type":"assistant","message":{"id":"m2","type":"message","role":"assistant","model":"claude-sonnet-9","content":[],"usage":{"input_tokens":100000,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":1}},"session_id":"s","uuid":"u2"}""";
        // Two entries, neither of them the model that answered — so the single-entry fallback cannot rescue it either.
        const string secondResult = """{"type":"result","subtype":"success","session_id":"s","modelUsage":{"claude-haiku-4-5":{"contextWindow":200000},"claude-opus-5":{"contextWindow":1000000}},"is_error":false}""";
        // Carried so the pill still has something to show, which keeps this an assertion about the context figure
        // alone rather than about the snapshot disappearing wholesale.
        const string limit = """{"type":"rate_limit_event","rate_limit_info":{"rateLimitType":"five_hour","utilization":0.4},"uuid":"u","session_id":"s"}""";

        var status = Observe(firstAssistant, firstResult, secondAssistant, secondResult, limit).Status;

        Assert.NotNull(status);
        Assert.Null(status.ContextUsedPercent);
        Assert.Equal("5h", Assert.Single(status.RateLimits).Label);
    }

    [Fact]
    public void AResultLineNamingNoModelsAtAll_LeavesTheEstablishedWindowStanding()
    {
        // An errored turn carries no modelUsage. That is the CLI saying nothing about the window, not saying the
        // window changed — so the last good reading survives instead of the pill blanking on a transient failure.
        var status = Observe([.. RealTurn, """{"type":"result","subtype":"error_during_execution","session_id":"s","is_error":true,"errors":["boom"]}"""]).Status;

        Assert.NotNull(status);
        Assert.Equal(3d, status.ContextUsedPercent);
    }

    // The real capture, exposed for the driver-level test that pushes it down an actual stdout pump.
    internal static IReadOnlyList<string> RealTurnLines => RealTurn;
}
