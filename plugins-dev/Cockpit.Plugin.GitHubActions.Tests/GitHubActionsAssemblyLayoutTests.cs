extern alias UiAsm;

using GitHubActionsUi = UiAsm::Cockpit.Plugin.GitHubActions.UI.GitHubActionsUi;

namespace Cockpit.Plugin.GitHubActions.Tests;

// AC-1394: the two-assembly layout AC-1390 pilots (GitStatusAssemblyLayoutTests), applied to this plugin — the
// backend part names no Avalonia, and the UI part never reaches into the backend. UiAsm alias: see the .csproj
// comment on the UI reference.
public class GitHubActionsAssemblyLayoutTests
{
    [Theory]
    [InlineData(typeof(GitHubActionsPlugin), "Avalonia")]
    [InlineData(typeof(GitHubActionsUi), "Cockpit.Plugin.GitHubActions")]
    public void APart_DoesNotReferenceWhatItMustNot(Type entryType, string forbidden)
    {
        var references = entryType.Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => name == forbidden || name.StartsWith(forbidden + ".", StringComparison.Ordinal));

        Assert.Empty(references);
    }
}
