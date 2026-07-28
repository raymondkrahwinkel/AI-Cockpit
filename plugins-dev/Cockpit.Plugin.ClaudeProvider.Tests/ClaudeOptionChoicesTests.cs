namespace Cockpit.Plugin.ClaudeProvider.Tests;

/// <summary>
/// Guards the model vocabulary the operator picks from. The values are the CLI's own aliases, which it re-points at
/// each new release — so a label may never name a release (it would keep claiming one the dropdown no longer
/// launches), and every offered alias must carry a label or the dropdown shows a raw value (AC-418).
/// </summary>
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
}
