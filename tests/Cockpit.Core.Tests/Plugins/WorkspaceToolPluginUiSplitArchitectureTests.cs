using System.Text.Json;
using System.Xml.Linq;

namespace Cockpit.Core.Tests.Plugins;

// AC-1395 acceptance 1: the 9 workplace/tool plugins split per F2.1, mirrors ProviderPluginUiSplitArchitectureTests.
// Two (UsageTrend, SessionReview) kept a backend part; the other seven had none left once their D6 actions moved
// onto ICockpitUiHost, so the whole project became the UI project instead — one Theory per shape.
public class WorkspaceToolPluginUiSplitArchitectureTests
{
    [Theory]
    [InlineData("UsageTrend")]
    [InlineData("SessionReview")]
    public void ASplitPlugin_BackendNamesNoAvalonia_AndItsUiProject_NeverReferencesTheBackend(string plugin)
    {
        var pluginDirectory = Path.Combine(_RepositoryRoot(), "plugins-dev", $"Cockpit.Plugin.{plugin}");
        var backendProject = XDocument.Load(Path.Combine(pluginDirectory, $"Cockpit.Plugin.{plugin}.csproj"));
        var uiProject = XDocument.Load(Path.Combine(pluginDirectory, "UI", $"Cockpit.Plugin.{plugin}.UI.csproj"));

        var backendPackages = backendProject.Descendants("PackageReference").Select(reference => (string?)reference.Attribute("Include"));
        var uiProjectReferences = uiProject.Descendants("ProjectReference").Select(reference => (string?)reference.Attribute("Include"));

        Assert.DoesNotContain(backendPackages, name => name?.StartsWith("Avalonia", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(uiProjectReferences, include => include?.EndsWith($"Cockpit.Plugin.{plugin}.csproj", StringComparison.Ordinal) == true);
    }

    [Theory]
    [InlineData("Clock")]
    [InlineData("SystemMonitor")]
    [InlineData("ExampleWorkspace")]
    [InlineData("ExampleCompanionTool")]
    [InlineData("TranscriptSearch")]
    [InlineData("PromptLibrary")]
    [InlineData("FanOut")]
    public void APureUiPlugin_HasNoUiSubfolder_AndItsManifestNamesOnlyAUiAssembly(string plugin)
    {
        var pluginDirectory = Path.Combine(_RepositoryRoot(), "plugins-dev", $"Cockpit.Plugin.{plugin}");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(pluginDirectory, "plugin.json")));

        Assert.False(Directory.Exists(Path.Combine(pluginDirectory, "UI")));
        Assert.False(manifest.RootElement.TryGetProperty("entryAssembly", out _));
        Assert.True(manifest.RootElement.TryGetProperty("uiAssembly", out _));
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
