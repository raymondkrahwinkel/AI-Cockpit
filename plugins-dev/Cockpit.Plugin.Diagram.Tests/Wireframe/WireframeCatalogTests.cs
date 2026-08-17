using Cockpit.Plugin.Diagram.Wireframe;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Plugin.Diagram.Tests.Wireframe;

public sealed class WireframeCatalogTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("wireframe-catalog-tests-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string _Wireframes(string home)
    {
        var dir = Path.Combine(_root, home, "Wireframes");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void _Write(string dir, string slug, string title, string wireframe) =>
        File.WriteAllText(Path.Combine(dir, $"{slug}.md"), $"# {title}\n\n```wireframe\n{wireframe}\n```\n");

    [Fact]
    public void List_reads_title_and_wireframe_from_each_file()
    {
        var dir = _Wireframes("home");
        _Write(dir, "instellingen", "Instellingen", "screen \"Instellingen\"\n  button \"Opslaan\"");

        var rows = new[] { new ProjectMemoryRow(Path.Combine(_root, "home"), null, ReachesSessions: true) };
        var entries = WireframeCatalog.List(rows);

        var entry = Assert.Single(entries);
        Assert.Equal("Instellingen", entry.Title);
        Assert.Equal("screen \"Instellingen\"\n  button \"Opslaan\"", entry.WireframeText);
    }

    [Fact]
    public void List_skips_scheme_rows_and_rows_with_no_wireframes_folder_yet()
    {
        var rows = new[]
        {
            new ProjectMemoryRow("depot:cockpit", null, ReachesSessions: true),
            new ProjectMemoryRow(Path.Combine(_root, "unused"), null, ReachesSessions: true),
        };

        Assert.Empty(WireframeCatalog.List(rows));
    }

    [Fact]
    public void List_falls_back_to_the_filename_when_there_is_no_heading()
    {
        var dir = _Wireframes("home");
        File.WriteAllText(Path.Combine(dir, "untitled.md"), "```wireframe\nscreen \"Untitled\"\n```\n");

        var rows = new[] { new ProjectMemoryRow(Path.Combine(_root, "home"), null, ReachesSessions: true) };
        var entry = Assert.Single(WireframeCatalog.List(rows));

        Assert.Equal("untitled", entry.Title);
    }

    [Fact]
    public void Rename_replaces_only_the_heading_and_keeps_the_file_path()
    {
        var dir = _Wireframes("home");
        _Write(dir, "wf", "Old Title", "screen \"Old\"");
        var filePath = Path.Combine(dir, "wf.md");

        WireframeCatalog.Rename(filePath, "New Title");

        Assert.True(File.Exists(filePath));
        var rows = new[] { new ProjectMemoryRow(Path.Combine(_root, "home"), null, ReachesSessions: true) };
        var entry = Assert.Single(WireframeCatalog.List(rows));
        Assert.Equal("New Title", entry.Title);
        Assert.Equal("screen \"Old\"", entry.WireframeText);
    }

    [Fact]
    public void Create_writes_a_wireframe_the_list_finds_back()
    {
        var home = Path.Combine(_root, "home");
        var path = WireframeCatalog.Create(home, "Mijn Scherm", "screen \"Mijn Scherm\"");

        Assert.Equal(Path.Combine(home, "Wireframes", "mijn-scherm.md"), path);
        var entry = Assert.Single(WireframeCatalog.List([new ProjectMemoryRow(home, null, ReachesSessions: true)]));
        Assert.Equal("Mijn Scherm", entry.Title);
        Assert.Equal("screen \"Mijn Scherm\"", entry.WireframeText);
    }

    [Fact]
    public void Create_keeps_the_path_stable_and_suffixes_a_colliding_slug()
    {
        var home = Path.Combine(_root, "home");
        var first = WireframeCatalog.Create(home, "Scherm", "screen \"A\"");
        var second = WireframeCatalog.Create(home, "Scherm", "screen \"B\"");

        Assert.Equal(Path.Combine(home, "Wireframes", "scherm.md"), first);
        Assert.Equal(Path.Combine(home, "Wireframes", "scherm-2.md"), second);

        // A later save of the same wireframe overwrites its own file — the path never follows the title (AC-812).
        WireframeCatalog.Rename(first, "Heel Andere Naam");
        WireframeCatalog.Write(first, "Heel Andere Naam", "screen \"A\"\n  button \"Ga\"");
        Assert.True(File.Exists(first));
        Assert.Equal(2, WireframeCatalog.List([new ProjectMemoryRow(home, null, ReachesSessions: true)]).Count);
    }

    [Fact]
    public void Write_refuses_when_the_file_changed_underneath()
    {
        var path = WireframeCatalog.Create(Path.Combine(_root, "home"), "Scherm", "screen \"A\"");
        var asOpened = File.ReadAllText(path);
        File.WriteAllText(path, "# Scherm\n\n```wireframe\nscreen \"Elders gewijzigd\"\n```\n");

        Assert.Throws<IOException>(() => WireframeCatalog.Write(path, "Scherm", "screen \"A\"\n  button \"Ga\"", asOpened));
        Assert.Contains("Elders gewijzigd", File.ReadAllText(path));
    }

    [Fact]
    public void WritableHomes_keeps_folder_rows_and_drops_scheme_rows()
    {
        var folder = Path.Combine(_root, "home");
        var homes = WireframeCatalog.WritableHomes(
        [
            new ProjectMemoryRow("depot:cockpit", null, ReachesSessions: true),
            new ProjectMemoryRow(folder, "Projectmap", ReachesSessions: true),
        ]);

        Assert.Equal(folder, Assert.Single(homes).Reference);
        Assert.Empty(WireframeCatalog.WritableHomes([new ProjectMemoryRow("depot:cockpit", null, ReachesSessions: true)]));
    }

    [Fact]
    public void Delete_removes_the_file()
    {
        var dir = _Wireframes("home");
        _Write(dir, "wf", "Title", "screen \"A\"");
        var filePath = Path.Combine(dir, "wf.md");

        WireframeCatalog.Delete(filePath);

        Assert.False(File.Exists(filePath));
    }
}
