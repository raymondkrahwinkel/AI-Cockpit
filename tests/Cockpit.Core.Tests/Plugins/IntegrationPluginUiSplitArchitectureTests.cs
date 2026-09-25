using System.Xml.Linq;

namespace Cockpit.Core.Tests.Plugins;

// AC-1394 acceptance 1: the nine integration plugins split their UI into its own project, following GitStatus's
// pilot (AC-1390). One Theory over the nine, read from the csproj files directly (no assembly load needed) —
// same shape as ProviderPluginUiSplitArchitectureTests (AC-1393), which does this for the eight provider plugins.
public class IntegrationPluginUiSplitArchitectureTests
{
    // The UI folder's casing per plugin: most reused the folder that already held their settings control
    // (Ui, lowercase); Discord, Slack and GitHubActions had none and created UI (uppercase), matching GitStatus.
    [Theory]
    [InlineData("Depot", "Ui")]
    [InlineData("Discord", "UI")]
    [InlineData("Docker", "Ui")]
    [InlineData("Kind", "Ui")]
    [InlineData("Kubernetes", "Ui")]
    [InlineData("Proxmox", "Ui")]
    [InlineData("Slack", "UI")]
    [InlineData("LocalCi", "Ui")]
    [InlineData("GitHubActions", "UI")]
    public void TheBackendProject_NamesNoAvalonia_AndTheUiProject_NeverReferencesTheBackend(string plugin, string uiFolder)
    {
        var pluginDirectory = Path.Combine(_RepositoryRoot(), "plugins-dev", $"Cockpit.Plugin.{plugin}");
        var backendProject = XDocument.Load(Path.Combine(pluginDirectory, $"Cockpit.Plugin.{plugin}.csproj"));
        var uiProject = XDocument.Load(Path.Combine(pluginDirectory, uiFolder, $"Cockpit.Plugin.{plugin}.UI.csproj"));

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
