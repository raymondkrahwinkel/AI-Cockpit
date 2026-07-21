using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Mcp;
using FluentAssertions;
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
        item.TokenLabel.Should().BeEmpty("no estimate has been computed yet");

        item.IsEstimatingTokens = true;
        item.TokenLabel.Should().Be("…");

        item.IsEstimatingTokens = false;
        item.TokenEstimate = McpServerToolEstimate.Unavailable("youtrack");
        item.TokenLabel.Should().Be("?");

        item.TokenEstimate = new McpServerToolEstimate("youtrack", ToolCount: 6, EstimatedTokens: 4200, Available: true);
        item.TokenLabel.Should().Be("~4.2k");
    }

    [Fact]
    public void TokenTooltip_ExplainsTheFigure_EspeciallyTheUnknownCase()
    {
        var item = new McpServerSelectionItemViewModel("cockpit-workflows");
        item.TokenTooltip.Should().BeNull("nothing to explain before an estimate exists");

        item.IsEstimatingTokens = true;
        item.TokenTooltip.Should().Be("Counting this server's tools…");
        item.IsEstimatingTokens = false;

        // The "?" is the case worth a hover — a server that could not be reached reads as unknown, not zero.
        item.TokenEstimate = McpServerToolEstimate.Unavailable("cockpit-workflows");
        item.TokenTooltip.Should().Contain("Couldn't reach this server");

        item.TokenEstimate = new McpServerToolEstimate("cockpit-workflows", ToolCount: 1, EstimatedTokens: 300, Available: true);
        item.TokenTooltip.Should().Be("1 tool, ~300 tokens (estimate)", "one tool is singular");

        item.TokenEstimate = new McpServerToolEstimate("cockpit-workflows", ToolCount: 6, EstimatedTokens: 4200, Available: true);
        item.TokenTooltip.Should().Be("6 tools, ~4.2k tokens (estimate)");
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

        tokens.Should().Be(1500, "only the ticked, known rows count — the unticked 9000 is excluded");
        anyEstimating.Should().BeTrue();
        anyUnknown.Should().BeTrue();
    }

    [Fact]
    public void SummaryLabel_ReadsAsAToolsOnlyEstimate_AndCallsOutEstimatingAndUnknown()
    {
        McpTokenEstimation.SummaryLabel([_Item("a", 4200)])
            .Should().Be("MCP tools: ~4.2k tokens (estimate, tools only)");

        var unknown = _Item("b", 0);
        unknown.TokenEstimate = McpServerToolEstimate.Unavailable("b");
        McpTokenEstimation.SummaryLabel([_Item("a", 1000), unknown])
            .Should().Be("MCP tools: ~1k tokens (estimate, tools only) + some unknown");

        var estimating = _Item("c", 0);
        estimating.IsEstimatingTokens = true;
        estimating.TokenEstimate = null;
        McpTokenEstimation.SummaryLabel([_Item("a", 1000), estimating]).Should().Be("MCP tools: estimating…");
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

        items[0].TokenEstimate!.EstimatedTokens.Should().Be(1200);
        items[1].TokenEstimate!.EstimatedTokens.Should().Be(9000);
        items.Should().OnlyContain(item => !item.IsEstimatingTokens);
        McpTokenEstimation.Total(items).Tokens.Should().Be(10200);
    }

    private static McpServerSelectionItemViewModel _Item(string name, int tokens) =>
        new(name) { TokenEstimate = new McpServerToolEstimate(name, ToolCount: 1, EstimatedTokens: tokens, Available: true) };
}
