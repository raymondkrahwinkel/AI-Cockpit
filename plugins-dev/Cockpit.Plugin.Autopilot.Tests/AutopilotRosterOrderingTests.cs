using Cockpit.Plugins.Abstractions.Profiles;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.Autopilot.Tests;

// What the roster claims about model cost (AC-256). The defect these exist for was not a crash: the brief told the CEO
// its model list ran cheapest-first while the list ran the other way, and the CEO obeyed it straight into the most
// expensive tier. So these assert the claim against the order it was built from, not merely that a claim is present.
public class AutopilotRosterOrderingTests
{
    private static readonly IReadOnlyList<PluginProfileInfo> Ranked =
    [
        new PluginProfileInfo("Claude", "Plugin", string.Empty)
        {
            ModelSuggestions = ["fable", "opus", "sonnet", "haiku"],
            ModelCostEstimatesCheapestFirst =
            [
                new PluginModelCostEstimate("haiku") { EstimatedInputUsdPerMillionTokens = 1m, EstimatedOutputUsdPerMillionTokens = 5m },
                new PluginModelCostEstimate("sonnet") { EstimatedInputUsdPerMillionTokens = 2.5m, EstimatedOutputUsdPerMillionTokens = 15m },
                new PluginModelCostEstimate("opus") { EstimatedInputUsdPerMillionTokens = 5m, EstimatedOutputUsdPerMillionTokens = 25m },
            ],
        },
    ];

    private static string Brief(IReadOnlyList<PluginProfileInfo> profiles, AutopilotCostStrategy strategy = AutopilotCostStrategy.Balanced) =>
        AutopilotCeoBrief.For(AutopilotPlan.Empty(source: null, goal: "Build a feature"), profiles, costStrategy: strategy);

    [Fact]
    public void Roster_ListsTheModelsInTheOrderTheProviderRanked()
    {
        // The assertion that catches the original bug: reverse the rendering and this fails, because it compares the
        // rendered positions against the ranking rather than against a hardcoded expectation.
        var brief = Brief(Ranked);
        var ranking = Ranked[0].ModelCostEstimatesCheapestFirst;

        var positions = ranking.Select(estimate => brief.IndexOf(estimate.Model, StringComparison.Ordinal)).ToList();

        Assert.DoesNotContain(-1, positions);
        Assert.Equal(positions.OrderBy(position => position), positions);
    }

    [Fact]
    public void Roster_SaysWhichEndOfTheListIsCheap() =>
        Assert.Contains("models cheapest first", Brief(Ranked), StringComparison.Ordinal);

    [Fact]
    public void Roster_WhenTheProviderRankedNothing_ClaimsNoOrderAtAll()
    {
        // Silence is not enough here: an unlabelled list invites the reader to assume an order, which is exactly the
        // assumption that went wrong. It has to say out loud that there is none.
        IReadOnlyList<PluginProfileInfo> unranked = [new PluginProfileInfo("Codex", "Plugin", string.Empty) { ModelSuggestions = ["gpt-x", "gpt-y"] }];

        var brief = Brief(unranked);

        Assert.Contains("in no particular order", brief, StringComparison.Ordinal);
        Assert.DoesNotContain("models cheapest first", brief, StringComparison.Ordinal);
    }

    [Fact]
    public void Roster_CallsEveryPriceAnEstimate()
    {
        var brief = Brief(Ranked);

        Assert.Contains("est. $1/$5", brief, StringComparison.Ordinal);
        Assert.Contains("provider's own estimate", brief, StringComparison.Ordinal);
        Assert.Contains("may be out of date", brief, StringComparison.Ordinal);
    }

    [Fact]
    public void Roster_FormatsPricesTheSameOnEveryMachine() =>
        // The brief is English prose; a machine set to a comma decimal separator must not render "$2,5" into it.
        Assert.Contains("est. $2.5/$15", Brief(Ranked), StringComparison.Ordinal);

    [Fact]
    public void Roster_WithoutPrices_StillRanksButQuotesNothing()
    {
        IReadOnlyList<PluginProfileInfo> pricelessRanking =
        [
            new PluginProfileInfo("Claude", "Plugin", string.Empty)
            {
                ModelSuggestions = ["b", "a"],
                ModelCostEstimatesCheapestFirst = [new PluginModelCostEstimate("a"), new PluginModelCostEstimate("b")],
            },
        ];

        var brief = Brief(pricelessRanking);

        Assert.Contains("models cheapest first: a, b", brief, StringComparison.Ordinal);
        Assert.DoesNotContain("est. $", brief, StringComparison.Ordinal);
    }

    [Fact]
    public void Roster_TellsTheCeoTheCeilingIsEnforced()
    {
        // Without this the CEO only discovers the ceiling by being rejected, which costs a whole redraft.
        var balanced = Brief(Ranked, AutopilotCostStrategy.Balanced);
        Assert.Contains("cheaper half", balanced, StringComparison.Ordinal);
        Assert.Contains("rejected", balanced, StringComparison.Ordinal);

        var costFirst = Brief(Ranked, AutopilotCostStrategy.CostFirst);
        Assert.Contains("cheapest model its profile lists", costFirst, StringComparison.Ordinal);
        Assert.Contains("rejected", costFirst, StringComparison.Ordinal);
    }

    [Fact]
    public void Roster_UnderQualityFirst_SaysThereIsNoCeiling() =>
        Assert.Contains("No cost ceiling applies", Brief(Ranked, AutopilotCostStrategy.QualityFirst), StringComparison.Ordinal);
}
