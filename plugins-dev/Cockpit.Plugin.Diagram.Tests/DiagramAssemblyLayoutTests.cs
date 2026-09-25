extern alias backend;

using DiagramPlugin = backend::Cockpit.Plugin.Diagram.DiagramPlugin;

namespace Cockpit.Plugin.Diagram.Tests;

// The two-assembly layout GitStatus pilots for every plugin (AC-1390), applied to Diagram (F2.13/AC-1401): one
// build of the backend project leaves both parts in one folder, the backend part names no Avalonia, and the UI
// part never reaches into the backend.
public class DiagramAssemblyLayoutTests
{
    [Fact]
    public void BuildingTheBackendProject_LeavesBothPartsInItsOutputFolder()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Release";
        var output = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "Cockpit.Plugin.Diagram", "bin", configuration, "net10.0"));

        var files = Directory.GetFiles(output).Select(Path.GetFileName).ToList();

        Assert.Contains("Cockpit.Plugin.Diagram.dll", files);
        Assert.Contains("Cockpit.Plugin.Diagram.deps.json", files);
        Assert.Contains("Cockpit.Plugin.Diagram.UI.dll", files);
        Assert.Contains("Cockpit.Plugin.Diagram.UI.deps.json", files);
        Assert.Contains("plugin.json", files);
    }

    [Theory]
    [InlineData(typeof(DiagramPlugin), "Avalonia")]
    [InlineData(typeof(DiagramUi), "Cockpit.Plugin.Diagram")]
    public void APart_DoesNotReferenceWhatItMustNot(Type entryType, string forbidden)
    {
        var references = entryType.Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => name == forbidden || name.StartsWith(forbidden + ".", StringComparison.Ordinal));

        Assert.Empty(references);
    }
}
