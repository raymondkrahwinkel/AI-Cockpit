using Cockpit.Plugin.Diagram.Whiteboard;
using Cockpit.Plugin.Diagram.Whiteboard.Model;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Plugin.Diagram.Tests.Whiteboard;

public sealed class WhiteboardCatalogTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("whiteboard-catalog-tests-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // W-2/AC-843's DoD in one test: bord vullen -> sluiten -> heropenen -> document is identiek. "Sluiten" and
    // "heropenen" are Create (first save) followed by Load from that same file — nothing kept in memory between.
    [Fact]
    public void Create_then_Load_bringsEveryObjectBackWithAllItsData()
    {
        var document = new WhiteboardDocument(title: "plan-schets");
        var image = new PlacedObject
        {
            ShapeKind = PlacedShapeKind.Image,
            X = 40, Y = 40, Width = 100, Height = 80,
            ImageData = [1, 2, 3, 4, 5],
            IsPastedScreenshot = true,
        };
        document.Add(new FreehandStroke
        {
            Points = [new WhiteboardPoint(1, 2), new WhiteboardPoint(3, 4)],
            Thickness = 14,
            IsMarker = true,
            ParentImageId = image.Id,
        });
        document.Add(new PlacedObject
        {
            ShapeKind = PlacedShapeKind.StickyNote,
            X = 10, Y = 20, Width = 140, Height = 140,
            Text = "hallo",
        });
        document.Add(image);
        document.Add(new PlacedObject
        {
            ShapeKind = PlacedShapeKind.Rectangle,
            X = 5, Y = 5, Width = 50, Height = 30,
            PlacedByAgent = true,
        });

        var home = Path.Combine(_root, "home");
        var path = WhiteboardCatalog.Create(home, document);

        Assert.Equal(Path.Combine(home, "Whiteboards", "plan-schets.json"), path);

        var reopened = WhiteboardCatalog.Load(path);
        Assert.Equal("plan-schets", reopened.Title);
        Assert.Equal(4, reopened.Objects.Count);

        var stroke = Assert.IsType<FreehandStroke>(reopened.Objects.Single(o => o.Kind == WhiteboardObjectKind.Freehand));
        Assert.Equal([new WhiteboardPoint(1, 2), new WhiteboardPoint(3, 4)], stroke.Points);
        Assert.Equal(14, stroke.Thickness);
        Assert.True(stroke.IsMarker);

        var sticky = Assert.IsType<PlacedObject>(reopened.Objects.Single(o => o is PlacedObject { ShapeKind: PlacedShapeKind.StickyNote }));
        Assert.Equal("hallo", sticky.Text);
        Assert.Equal(10, sticky.X);
        Assert.Equal(140, sticky.Width);

        var reopenedImage = Assert.IsType<PlacedObject>(reopened.Objects.Single(o => o is PlacedObject { ShapeKind: PlacedShapeKind.Image }));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, reopenedImage.ImageData);
        Assert.True(reopenedImage.IsPastedScreenshot);

        var agentPlaced = Assert.IsType<PlacedObject>(reopened.Objects.Single(o => o is PlacedObject { ShapeKind: PlacedShapeKind.Rectangle }));
        Assert.True(agentPlaced.PlacedByAgent);

        // W-6/AC-851: the binding between the stroke and the image it was drawn on survives the round trip too.
        Assert.Equal(reopenedImage.Id, stroke.ParentImageId);
        Assert.Null(sticky.ParentImageId);
    }

    // AC-916 AC2: a board saved by an older build has no "color" property at all — JsonOptions already skips
    // unknown/missing members, so this is a fact worth locking down, not a migration to write.
    [Fact]
    public void Load_ofABoardSavedWithoutColor_LeavesColorNull()
    {
        var home = Path.Combine(_root, "home");
        var directory = Path.Combine(home, "Whiteboards");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "oud-bord.json");
        File.WriteAllText(path, """
            {
              "title": "Oud bord",
              "objects": [
                { "id": "11111111-1111-1111-1111-111111111111", "kind": "Placed", "shapeKind": "Rectangle", "x": 5, "y": 5, "width": 50, "height": 30 }
              ]
            }
            """);

        var reopened = WhiteboardCatalog.Load(path);

        Assert.Null(Assert.Single(reopened.Objects).Color);
    }

    [Fact]
    public void Create_then_Load_roundTrips_theColour()
    {
        var document = new WhiteboardDocument(title: "gekleurd-bord");
        document.Add(new PlacedObject { ShapeKind = PlacedShapeKind.Rectangle, X = 5, Y = 5, Width = 50, Height = 30, Color = "#DC2626" });

        var path = WhiteboardCatalog.Create(Path.Combine(_root, "home"), document);
        var reopened = WhiteboardCatalog.Load(path);

        Assert.Equal("#DC2626", Assert.Single(reopened.Objects).Color);
    }

    [Fact]
    public void List_reads_the_title_from_each_saved_board()
    {
        var home = Path.Combine(_root, "home");
        WhiteboardCatalog.Create(home, new WhiteboardDocument(title: "Bord Een"));
        WhiteboardCatalog.Create(home, new WhiteboardDocument(title: "Bord Twee"));

        var entries = WhiteboardCatalog.List([new ProjectMemoryRow(home, null, ReachesSessions: true)]);

        Assert.Equal(["Bord Een", "Bord Twee"], entries.Select(e => e.Title).OrderBy(t => t));
    }

    [Fact]
    public void Create_keeps_the_path_stable_and_suffixes_a_colliding_slug()
    {
        var home = Path.Combine(_root, "home");
        var first = WhiteboardCatalog.Create(home, new WhiteboardDocument(title: "Bord"));
        var second = WhiteboardCatalog.Create(home, new WhiteboardDocument(title: "Bord"));

        Assert.Equal(Path.Combine(home, "Whiteboards", "bord.json"), first);
        Assert.Equal(Path.Combine(home, "Whiteboards", "bord-2.json"), second);
    }

    [Fact]
    public void Write_refuses_when_the_file_changed_underneath()
    {
        var path = WhiteboardCatalog.Create(Path.Combine(_root, "home"), new WhiteboardDocument(title: "Bord"));
        var asOpened = File.ReadAllText(path);
        File.WriteAllText(path, "{\"title\":\"Elders gewijzigd\"}");

        Assert.Throws<IOException>(() => WhiteboardCatalog.Write(path, new WhiteboardDocument(title: "Bord"), asOpened));
        Assert.Contains("Elders gewijzigd", File.ReadAllText(path));
    }

    [Fact]
    public void WritableHomes_keeps_folder_rows_and_drops_scheme_rows()
    {
        var folder = Path.Combine(_root, "home");
        var homes = WhiteboardCatalog.WritableHomes(
        [
            new ProjectMemoryRow("depot:cockpit", null, ReachesSessions: true),
            new ProjectMemoryRow(folder, "Projectmap", ReachesSessions: true),
        ]);

        Assert.Equal(folder, Assert.Single(homes).Reference);
    }

    [Fact]
    public void Delete_removes_the_file()
    {
        var path = WhiteboardCatalog.Create(Path.Combine(_root, "home"), new WhiteboardDocument(title: "Bord"));

        WhiteboardCatalog.Delete(path);

        Assert.False(File.Exists(path));
    }
}
