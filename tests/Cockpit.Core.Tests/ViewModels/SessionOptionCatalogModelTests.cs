using Cockpit.App.ViewModels;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// Guards the model vocabulary offered wherever a Claude model is chosen from the host (a profile's session
/// defaults, the running session's model field, and the roster Autopilot may plan with). The values are the CLI's
/// own aliases, so no label may name a release, and the app default is pinned by value — a positional default moves
/// the moment a model is added to the list (AC-418).
/// </summary>
public class SessionOptionCatalogModelTests
{
    [Fact]
    public void Models_OfferTheCliAliases_IncludingFable()
    {
        Assert.Equal(new[] { "fable", "opus", "sonnet", "haiku" }, SessionOptionCatalog.Models.Select(model => model.Value));
    }

    [Fact]
    public void ModelLabels_NameNoRelease_SoALabelCannotOutliveWhatTheAliasResolvesTo()
    {
        foreach (var model in SessionOptionCatalog.Models)
        {
            Assert.False(model.Label.Any(char.IsDigit), $"model label '{model.Label}' for alias '{model.Value}' names a specific release");
        }
    }

    [Fact]
    public void DefaultModel_IsSonnet_WhereverItSitsInTheList()
    {
        Assert.Equal("sonnet", SessionOptionCatalog.DefaultModel.Value);
    }
}
