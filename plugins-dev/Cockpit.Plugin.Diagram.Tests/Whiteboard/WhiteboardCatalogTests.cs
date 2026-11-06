using Cockpit.Plugin.Diagram.Whiteboard;
using Cockpit.Plugin.Diagram.Whiteboard.Model;

namespace Cockpit.Plugin.Diagram.Tests.Whiteboard;

public sealed class WhiteboardCatalogTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("whiteboard-catalog-tests-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // AC-916 AC2: a board saved by an older build has no "color" property at all — JsonOptions already skips
    // unknown/missing members, so this is a fact worth locking down, not a migration to write.

    [Fact]
    public void Write_refuses_when_the_file_changed_underneath()
    {
        var path = WhiteboardCatalog.Create(Path.Combine(_root, "home"), new WhiteboardDocument(title: "Bord"));
        var asOpened = File.ReadAllText(path);
        File.WriteAllText(path, "{\"title\":\"Elders gewijzigd\"}");

        Assert.Throws<IOException>(() => WhiteboardCatalog.Write(path, new WhiteboardDocument(title: "Bord"), asOpened));
        Assert.Contains("Elders gewijzigd", File.ReadAllText(path));
    }
}
