namespace Cockpit.Plugin.ClaudeProvider.Tests;

// Guards the model vocabulary the operator picks from. The values are the CLI's own aliases, which it re-points at
// each new release — so a label may never name a release (it would keep claiming one the dropdown no longer
// launches), and every offered alias must carry a label or the dropdown shows a raw value (AC-418).
public class ClaudeOptionChoicesTests
{
    [Fact]
    public void ModelFacts_DoNotInventPricesJoinKnownPublicModelsAndCoverOfferedAliases()
    {
        // The list order is the claim consumers route on (AC-256), so it has to agree with the prices beside it —
        // reorder one entry without moving its price and this fails, which is the whole point of the field.
        Assert.All(ClaudeOptionChoices.ModelCostEstimatesCheapestFirst, estimate =>
        {
            Assert.Null(estimate.EstimatedInputUsdPerMillionTokens);
            Assert.Null(estimate.EstimatedOutputUsdPerMillionTokens);
        });
        Assert.Equal("~anthropic/claude-haiku-latest", ClaudeOptionChoices.ModelCostEstimatesCheapestFirst.Single(estimate => estimate.Model == "haiku").PublicModelRef);
        Assert.Equal("~anthropic/claude-sonnet-latest", ClaudeOptionChoices.ModelCostEstimatesCheapestFirst.Single(estimate => estimate.Model == "sonnet").PublicModelRef);
        Assert.Equal("~anthropic/claude-opus-latest", ClaudeOptionChoices.ModelCostEstimatesCheapestFirst.Single(estimate => estimate.Model == "opus").PublicModelRef);
        Assert.Equal("~anthropic/claude-fable-latest", ClaudeOptionChoices.ModelCostEstimatesCheapestFirst.Single(estimate => estimate.Model == "fable").PublicModelRef);
        Assert.Equal(ClaudeOptionChoices.ModelCostEstimatesCheapestFirst, new ClaudeModelIntelligence().Read().Models);
        Assert.Equal(
            ClaudeOptionChoices.ModelSuggestions.OrderBy(alias => alias, StringComparer.Ordinal),
            ClaudeOptionChoices.ModelCostEstimatesCheapestFirst.Select(estimate => estimate.Model).OrderBy(alias => alias, StringComparer.Ordinal));
    }

}
