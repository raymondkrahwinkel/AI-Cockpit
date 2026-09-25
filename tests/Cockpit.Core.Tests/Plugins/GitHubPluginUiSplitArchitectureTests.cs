using System.Xml.Linq;

namespace Cockpit.Core.Tests.Plugins;

// AC-1396 acceptance 1: GitHubIssues and GitHubPullRequests split their UI into its own project, following
// GitStatus's pilot (AC-1390). Read from the csproj files directly, the same shape as
// IntegrationPluginUiSplitArchitectureTests (AC-1394) and ProviderPluginUiSplitArchitectureTests (AC-1393).
public class GitHubPluginUiSplitArchitectureTests
{
    [Theory]
    [InlineData("GitHubIssues")]
    [InlineData("GitHubPullRequests")]
    public void TheBackendProject_NamesNoAvalonia_AndTheUiProject_NeverReferencesTheBackend(string plugin)
    {
        var pluginDirectory = Path.Combine(_RepositoryRoot(), "plugins-dev", $"Cockpit.Plugin.{plugin}");
        var backendProject = XDocument.Load(Path.Combine(pluginDirectory, $"Cockpit.Plugin.{plugin}.csproj"));
        var uiProject = XDocument.Load(Path.Combine(pluginDirectory, "UI", $"Cockpit.Plugin.{plugin}.UI.csproj"));

        var backendPackages = backendProject.Descendants("PackageReference").Select(reference => (string?)reference.Attribute("Include"));
        var uiProjectReferences = uiProject.Descendants("ProjectReference").Select(reference => (string?)reference.Attribute("Include"));

        Assert.DoesNotContain(backendPackages, name => name?.StartsWith("Avalonia", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(uiProjectReferences, include => include?.EndsWith($"Cockpit.Plugin.{plugin}.csproj", StringComparison.Ordinal) == true);
    }

    private static string _RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Cockpit.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }
}
