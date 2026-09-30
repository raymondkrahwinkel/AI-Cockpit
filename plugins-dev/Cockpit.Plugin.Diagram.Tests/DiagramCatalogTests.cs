using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Plugin.Diagram.Tests;

public sealed class DiagramCatalogTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("diagram-catalog-tests-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Create_writes_a_diagram_the_list_finds_back()
    {
        var home = Path.Combine(_root, "home");
        var path = DiagramCatalog.Create(home, "Mijn Flow", "flowchart LR\n  A --> B");

        Assert.Equal(Path.Combine(home, "Diagrams", "mijn-flow.md"), path);
        var entry = Assert.Single(DiagramCatalog.List([new ProjectMemoryRow(home, null, ReachesSessions: true)]));
        Assert.Equal("Mijn Flow", entry.Title);
        Assert.Equal("flowchart LR\n  A --> B", entry.MermaidText);
    }

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
