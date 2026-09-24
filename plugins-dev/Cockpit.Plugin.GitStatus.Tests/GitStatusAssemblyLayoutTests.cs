using Cockpit.Plugin.GitStatus.UI;

namespace Cockpit.Plugin.GitStatus.Tests;

// The two-assembly layout GitStatus pilots for every plugin (AC-1390): one build of the backend project leaves
// both parts in one folder, the backend part names no Avalonia, and the UI part never reaches into the backend.
public class GitStatusAssemblyLayoutTests
{
    [Fact]
    public void BuildingTheBackendProject_LeavesBothPartsInItsOutputFolder()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Release";
        var output = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "Cockpit.Plugin.GitStatus", "bin", configuration, "net10.0"));

        var files = Directory.GetFiles(output).Select(Path.GetFileName).ToList();

        Assert.Contains("Cockpit.Plugin.GitStatus.dll", files);
        Assert.Contains("Cockpit.Plugin.GitStatus.deps.json", files);
        Assert.Contains("Cockpit.Plugin.GitStatus.UI.dll", files);
        Assert.Contains("Cockpit.Plugin.GitStatus.UI.deps.json", files);
        Assert.Contains("plugin.json", files);
    }

    [Theory]
    [InlineData(typeof(GitStatusPlugin), "Avalonia")]
    [InlineData(typeof(GitStatusUi), "Cockpit.Plugin.GitStatus")]
    public void APart_DoesNotReferenceWhatItMustNot(Type entryType, string forbidden)
    {
        var references = entryType.Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => name == forbidden || name.StartsWith(forbidden + ".", StringComparison.Ordinal));

        Assert.Empty(references);
    }
}
