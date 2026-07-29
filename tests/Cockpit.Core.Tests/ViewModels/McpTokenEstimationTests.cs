using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Mcp;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// AC-134: the shared MCP tool-token rollup behind the New-session dialog and the profile editor — the per-row
/// label, the running total over the ticked rows, and the background estimation pass.
/// </summary>
public class McpTokenEstimationTests
{
    [Fact]
    public void TokenLabel_ReflectsTheRowsEstimateState()
    {
        var item = new McpServerSelectionItemViewModel("youtrack");
        Assert.Empty(item.TokenLabel);

        item.IsEstimatingTokens = true;
        Assert.Equal("…", item.TokenLabel);

        item.IsEstimatingTokens = false;
        item.TokenEstimate = McpServerToolEstimate.Unavailable("youtrack");
        Assert.Equal("?", item.TokenLabel);

        item.TokenEstimate = new McpServerToolEstimate("youtrack", ToolCount: 6, EstimatedTokens: 4200, Available: true);
        Assert.Equal("~4.2k", item.TokenLabel);
    }

    [Fact]
    public void TokenTooltip_ExplainsTheFigure_EspeciallyTheUnknownCase()
    {
        var item = new McpServerSelectionItemViewModel("cockpit-workflows");
        Assert.Null(item.TokenTooltip);

        item.IsEstimatingTokens = true;
        Assert.Equal("Counting this server's tools…", item.TokenTooltip);
        item.IsEstimatingTokens = false;

        // The "?" is the case worth a hover — a server that could not be reached reads as unknown, not zero.
        item.TokenEstimate = McpServerToolEstimate.Unavailable("cockpit-workflows");
        Assert.Contains("Couldn't reach this server", item.TokenTooltip);

        item.TokenEstimate = new McpServerToolEstimate("cockpit-workflows", ToolCount: 1, EstimatedTokens: 300, Available: true);
        Assert.Equal("1 tool, ~300 tokens (estimate)", item.TokenTooltip);

        item.TokenEstimate = new McpServerToolEstimate("cockpit-workflows", ToolCount: 6, EstimatedTokens: 4200, Available: true);
        Assert.Equal("6 tools, ~4.2k tokens (estimate)", item.TokenTooltip);
    }

    [Fact]
    public void Total_SumsTheTickedAvailableRows_AndFlagsUnknownAndEstimating()
    {
        var ticked = _Item("a", tokens: 1000);
        var alsoTicked = _Item("b", tokens: 500);
        var unticked = _Item("c", tokens: 9000);
        unticked.IsEnabledForSession = false;
        var estimating = _Item("d", tokens: 0);
        estimating.IsEstimatingTokens = true;
        estimating.TokenEstimate = null;
        var unknown = _Item("e", tokens: 0);
        unknown.TokenEstimate = McpServerToolEstimate.Unavailable("e");

        var (tokens, anyEstimating, anyUnknown) = McpTokenEstimation.Total([ticked, alsoTicked, unticked, estimating, unknown]);

        Assert.Equal(1500, tokens);
        Assert.True(anyEstimating);
        Assert.True(anyUnknown);
    }

    [Fact]
    public void SummaryLabel_ReadsAsAToolsOnlyEstimate_AndCallsOutEstimatingAndUnknown()
    {
        Assert.Equal("MCP tools: ~4.2k tokens (estimate, tools only)", McpTokenEstimation.SummaryLabel([_Item("a", 4200)]));

        var unknown = _Item("b", 0);
        unknown.TokenEstimate = McpServerToolEstimate.Unavailable("b");
        Assert.Equal("MCP tools: ~1k tokens (estimate, tools only) + some unknown", McpTokenEstimation.SummaryLabel([_Item("a", 1000), unknown]));

        var estimating = _Item("c", 0);
        estimating.IsEstimatingTokens = true;
        estimating.TokenEstimate = null;
        Assert.Equal("MCP tools: estimating…", McpTokenEstimation.SummaryLabel([_Item("a", 1000), estimating]));
    }

    [Fact]
    public async Task EstimateAllAsync_EstimatesEachRow_AndClearsTheEstimatingFlag()
    {
        var items = new[] { new McpServerSelectionItemViewModel("youtrack"), new McpServerSelectionItemViewModel("docker") };
        var estimator = Substitute.For<IMcpToolTokenEstimator>();
        estimator.EstimateAsync("youtrack", Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new McpServerToolEstimate("youtrack", 3, 1200, true));
        estimator.EstimateAsync("docker", Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new McpServerToolEstimate("docker", 8, 9000, true));

        await McpTokenEstimation.EstimateAllAsync(items, estimator, refresh: false, CancellationToken.None);

        Assert.Equal(1200, items[0].TokenEstimate!.EstimatedTokens);
        Assert.Equal(9000, items[1].TokenEstimate!.EstimatedTokens);
        Assert.All(items, item => Assert.False(item.IsEstimatingTokens));
        Assert.Equal(10200, McpTokenEstimation.Total(items).Tokens);
    }

    private static McpServerSelectionItemViewModel _Item(string name, int tokens) =>
        new(name) { TokenEstimate = new McpServerToolEstimate(name, ToolCount: 1, EstimatedTokens: tokens, Available: true) };
}
