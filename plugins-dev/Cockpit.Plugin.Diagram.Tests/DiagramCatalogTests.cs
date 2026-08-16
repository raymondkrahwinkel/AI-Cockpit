using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Plugin.Diagram.Tests;

public sealed class DiagramCatalogTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("diagram-catalog-tests-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string _Diagrams(string home)
    {
        var dir = Path.Combine(_root, home, "Diagrams");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void _Write(string dir, string slug, string title, string mermaid) =>
        File.WriteAllText(Path.Combine(dir, $"{slug}.md"), $"# {title}\n\n```mermaid\n{mermaid}\n```\n");

    [Fact]
    public void List_reads_title_and_mermaid_from_each_file()
    {
        var dir = _Diagrams("home");
        _Write(dir, "flow", "The Flow", "flowchart LR\n  A --> B");

        var rows = new[] { new ProjectMemoryRow(Path.Combine(_root, "home"), null, ReachesSessions: true) };
        var entries = DiagramCatalog.List(rows);

        var entry = Assert.Single(entries);
        Assert.Equal("The Flow", entry.Title);
        Assert.Equal("flowchart LR\n  A --> B", entry.MermaidText);
    }

    [Fact]
    public void List_skips_scheme_rows_and_rows_with_no_diagrams_folder_yet()
    {
        var rows = new[]
        {
            new ProjectMemoryRow("depot:cockpit", null, ReachesSessions: true),
            new ProjectMemoryRow(Path.Combine(_root, "unused"), null, ReachesSessions: true),
        };

        Assert.Empty(DiagramCatalog.List(rows));
    }

    [Fact]
    public void List_falls_back_to_the_filename_when_there_is_no_heading()
    {
        var dir = _Diagrams("home");
        File.WriteAllText(Path.Combine(dir, "untitled.md"), "```mermaid\nflowchart LR\n  A --> B\n```\n");

        var rows = new[] { new ProjectMemoryRow(Path.Combine(_root, "home"), null, ReachesSessions: true) };
        var entry = Assert.Single(DiagramCatalog.List(rows));

        Assert.Equal("untitled", entry.Title);
    }

    [Fact]
    public void Rename_replaces_only_the_heading_and_keeps_the_file_path()
    {
        var dir = _Diagrams("home");
        _Write(dir, "flow", "Old Title", "flowchart LR\n  A --> B");
        var filePath = Path.Combine(dir, "flow.md");

        DiagramCatalog.Rename(filePath, "New Title");

        Assert.True(File.Exists(filePath));
        var rows = new[] { new ProjectMemoryRow(Path.Combine(_root, "home"), null, ReachesSessions: true) };
        var entry = Assert.Single(DiagramCatalog.List(rows));
        Assert.Equal("New Title", entry.Title);
        Assert.Equal("flowchart LR\n  A --> B", entry.MermaidText);
    }

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
    public void Create_keeps_the_path_stable_and_suffixes_a_colliding_slug()
    {
        var home = Path.Combine(_root, "home");
        var first = DiagramCatalog.Create(home, "Flow", "flowchart LR");
        var second = DiagramCatalog.Create(home, "Flow", "flowchart TD");

        Assert.Equal(Path.Combine(home, "Diagrams", "flow.md"), first);
        Assert.Equal(Path.Combine(home, "Diagrams", "flow-2.md"), second);

        // A later save of the same diagram overwrites its own file — the path never follows the title (AC-812).
        DiagramCatalog.Rename(first, "Heel Andere Naam");
        DiagramCatalog.Write(first, "Heel Andere Naam", "flowchart LR\n  A --> B");
        Assert.True(File.Exists(first));
        Assert.Equal(2, DiagramCatalog.List([new ProjectMemoryRow(home, null, ReachesSessions: true)]).Count);
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

    [Fact]
    public void WritableHomes_keeps_folder_rows_and_drops_scheme_rows()
    {
        var folder = Path.Combine(_root, "home");
        var homes = DiagramCatalog.WritableHomes(
        [
            new ProjectMemoryRow("depot:cockpit", null, ReachesSessions: true),
            new ProjectMemoryRow(folder, "Projectmap", ReachesSessions: true),
        ]);

        Assert.Equal(folder, Assert.Single(homes).Reference);
        Assert.Empty(DiagramCatalog.WritableHomes([new ProjectMemoryRow("depot:cockpit", null, ReachesSessions: true)]));
    }

    [Fact]
    public void Delete_removes_the_file()
    {
        var dir = _Diagrams("home");
        _Write(dir, "flow", "Title", "flowchart LR\n  A --> B");
        var filePath = Path.Combine(dir, "flow.md");

        DiagramCatalog.Delete(filePath);

        Assert.False(File.Exists(filePath));
    }
}
