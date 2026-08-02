namespace Cockpit.Plugin.ClaudeProvider.Tests;

// Guards the model vocabulary the operator picks from. The values are the CLI's own aliases, which it re-points at
// each new release — so a label may never name a release (it would keep claiming one the dropdown no longer
// launches), and every offered alias must carry a label or the dropdown shows a raw value (AC-418).
public class ClaudeOptionChoicesTests
{
    [Fact]
    public void ModelSuggestions_OfferTheCliAliases_IncludingFable()
    {
        Assert.Equal(new[] { "fable", "opus", "sonnet", "haiku" }, ClaudeOptionChoices.ModelSuggestions);
    }

    [Fact]
    public void ModelLabels_NameNoRelease_SoALabelCannotOutliveWhatTheAliasResolvesTo()
    {
        foreach (var (value, label) in ClaudeOptionChoices.ModelLabels)
        {
            Assert.False(label.Any(char.IsDigit), $"model label '{label}' for alias '{value}' names a specific release");
        }
    }

    [Fact]
    public void EveryModelSuggestion_CarriesALabel()
    {
        foreach (var suggestion in ClaudeOptionChoices.ModelSuggestions)
        {
            Assert.True(ClaudeOptionChoices.ModelLabels.ContainsKey(suggestion), $"model alias '{suggestion}' has no label");
        }
    }

    [Fact]
    public void ModelCostEstimates_AreOrderedCheapestFirst()
    {
        // The list order is the claim consumers route on (AC-256), so it has to agree with the prices beside it —
        // reorder one entry without moving its price and this fails, which is the whole point of the field.
        var input = ClaudeOptionChoices.ModelCostEstimatesCheapestFirst.Select(estimate => estimate.EstimatedInputUsdPerMillionTokens).ToList();
        var output = ClaudeOptionChoices.ModelCostEstimatesCheapestFirst.Select(estimate => estimate.EstimatedOutputUsdPerMillionTokens).ToList();

        Assert.Equal(input.OrderBy(price => price), input);
        Assert.Equal(output.OrderBy(price => price), output);
    }

    [Fact]
    public void ModelCostEstimates_CoverExactlyTheOfferedAliases()
    {
        // A model offered but unpriced silently drops out of the ceiling that keeps a run cheap; a priced model that
        // is not offered is a claim about something the operator can never pick.
        Assert.Equal(
            ClaudeOptionChoices.ModelSuggestions.OrderBy(alias => alias, StringComparer.Ordinal),
            ClaudeOptionChoices.ModelCostEstimatesCheapestFirst.Select(estimate => estimate.Model).OrderBy(alias => alias, StringComparer.Ordinal));
    }

    [Fact]
    public void ModelCostEstimates_QuoteBothDirections()
    {
        // Half a price reads as a fact while hiding the larger number: output tokens are the dearer side on every
        // model here, so an input-only figure would understate the very thing the ceiling exists to control.
        foreach (var estimate in ClaudeOptionChoices.ModelCostEstimatesCheapestFirst)
        {
            Assert.True(estimate.EstimatedInputUsdPerMillionTokens > 0, $"'{estimate.Model}' has no input price estimate");
            Assert.True(estimate.EstimatedOutputUsdPerMillionTokens > 0, $"'{estimate.Model}' has no output price estimate");
        }
    }
}
