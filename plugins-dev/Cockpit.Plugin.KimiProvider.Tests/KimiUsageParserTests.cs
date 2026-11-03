
namespace Cockpit.Plugin.KimiProvider.Tests;

// `KimiUsageParser` (AC-274) — the free-text `Context:` line is the only wire-level source of
// token/context data in Kimi's ACP surface (protocol §11); these tests pin the exact format and its pitfalls
// (thousands-comma counts, an above-100% percentage, the conditional Total/Current-turn rows) plus the
// "never guess at a number" fallback to `null` on anything unexpected.
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
}
