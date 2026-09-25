using System.Xml.Linq;

namespace Cockpit.Core.Tests.Plugins;

// AC-1393 acceptance 1: the eight provider plugins split their config view into UI/, following GitStatus's
// pilot (AC-1390). One Theory over the eight, read from the csproj files directly (no assembly load needed) —
// mirrors Cockpit.Backend.Tests.ArchitectureTests.OnlyTheUiHalfOfThePluginSdk_AndTheSdkUntilTheFlip_ReferenceAvalonia.
public class ProviderPluginUiSplitArchitectureTests
{
    [Theory]
    [InlineData("ClaudeProvider")]
    [InlineData("CliAgentProvider")]
    [InlineData("GeminiProvider")]
    [InlineData("GitHubModelsProvider")]
    [InlineData("GrokProvider")]
    [InlineData("KimiProvider")]
    [InlineData("OpencodeProvider")]
    [InlineData("OpenRouterProvider")]
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
