
namespace Cockpit.Plugin.Diagram.Tests;

public sealed class DiagramCatalogTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("diagram-catalog-tests-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Write_refuses_when_the_file_changed_underneath()
    {
        var path = DiagramCatalog.Create(Path.Combine(_root, "home"), "Flow", "flowchart LR");
        var asOpened = File.ReadAllText(path);
        File.WriteAllText(path, "# Flow\n\n```mermaid\nflowchart TD\n```\n");

        Assert.Throws<IOException>(() => DiagramCatalog.Write(path, "Flow", "flowchart LR\n  A --> B", asOpened));
        Assert.Contains("flowchart TD", File.ReadAllText(path));
    }
}
