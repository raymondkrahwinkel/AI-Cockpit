
namespace Cockpit.Plugin.KimiProvider.Tests;

/// <summary>
/// <see cref="KimiUsageParser"/> (AC-274) — the free-text <c>Context:</c> line is the only wire-level source of
/// token/context data in Kimi's ACP surface (protocol §11); these tests pin the exact format and its pitfalls
/// (thousands-comma counts, an above-100% percentage, the conditional Total/Current-turn rows) plus the
/// "never guess at a number" fallback to <see langword="null"/> on anything unexpected.
/// </summary>
public class KimiUsageParserTests
{
    [Fact]
    public void ParseContextUsedPercent_FullUsageBlockFromProtocolExample_ReturnsThePercentage()
    {
        const string usageBlock = """
            Session usage:
            - Total: input 12,345, output 6,789, cache read 0, cache creation 0
            - Current turn: input 1,000, output 200, cache read 0, cache creation 0
            - kimi-k2: input 12,345, output 6,789, cache read 0, cache creation 0
            - Context: 45,000 / 200,000 (22.5%)
            """;

        Assert.Equal(22.5, KimiUsageParser.ParseContextUsedPercent(usageBlock));
    }

    [Fact]
    public void ParseContextUsedPercent_ThousandsCommaCounts_AreParsedWithCommasStripped()
    {
        Assert.Equal(22.5, KimiUsageParser.ParseContextUsedPercent("- Context: 45,000 / 200,000 (22.5%)"));
    }

    // Percentage can exceed 100 ((contextUsage*100).toFixed(1)) — must not be clamped.
    [Fact]
    public void ParseContextUsedPercent_AboveOneHundredPercent_IsReturnedUnclamped()
    {
        Assert.Equal(105.0, KimiUsageParser.ParseContextUsedPercent("- Context: 210,000 / 200,000 (105.0%)"));
    }

    [Fact]
    public void ParseContextUsedPercent_OnlyTheContextLine_WithoutTotalOrCurrentTurn_StillWorks()
    {
        Assert.Equal(15.0, KimiUsageParser.ParseContextUsedPercent("- Context: 1,500 / 10,000 (15.0%)"));
    }

    // /status ends with the same "Context:" line shape (protocol §11) — no separate branch needed.
    [Fact]
    public void ParseContextUsedPercent_StatusBlock_ReusesTheSameContextLine()
    {
        const string statusBlock = """
            Session status:
            - Model: kimi-k2
            - Thinking: medium
            - Permission: manual
            - Plan mode: off
            - Context: 45,000 / 200,000 (22.5%)
            """;

        Assert.Equal(22.5, KimiUsageParser.ParseContextUsedPercent(statusBlock));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Session usage:\n- Total: input 12,345, output 6,789, cache read 0, cache creation 0")]
    [InlineData("garbage that is not a usage report at all")]
    [InlineData("- Context: not-a-number / also-not-a-number (nope%)")]
    [InlineData("- Context: 45,0x0 / 200,000 (22.5%)")]
    // A percentage in a different notation than expected (NL decimal comma, no decimal digit, no parentheses,
    // a stray space before the percent sign) must not be guessed at.
    [InlineData("- Context: 45,000 / 200,000 (22,5%)")]
    [InlineData("- Context: 45,000 / 200,000 (22%)")]
    [InlineData("- Context: 45,000 / 200,000 22.5%")]
    [InlineData("- Context: 45,000 / 200,000 (22.5 %)")]
    public void ParseContextUsedPercent_UnexpectedInput_ReturnsNull(string? text)
    {
        Assert.Null(KimiUsageParser.ParseContextUsedPercent(text));
    }

    // P1-12: proves the round-trip guard on the two token counts is genuinely reachable, contrary to the
    // reviewer's "the regex group can never produce anything long.TryParse refuses" claim — the regex's
    // (?:,\d{3})* has no upper bound on repeated groups, so a validly thousands-grouped count long enough to
    // overflow long (here 20 digits, past long.MaxValue's 19) still matches the pattern and must be rejected.
    [Fact]
    public void ParseContextUsedPercent_AThousandsGroupedCountThatOverflowsLong_ReturnsNull()
    {
        Assert.Null(KimiUsageParser.ParseContextUsedPercent("- Context: 99,999,999,999,999,999,999 / 200,000 (22.5%)"));
    }
}
