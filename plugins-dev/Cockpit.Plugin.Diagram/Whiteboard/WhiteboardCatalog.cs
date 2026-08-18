using System.Text.Json;
using System.Text.Json.Serialization;
using Cockpit.Core.Plugins;
using Cockpit.Core.Projects;
using Cockpit.Plugin.Diagram.Whiteboard.Model;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Plugin.Diagram.Whiteboard;

// One saved board found under a Memory row's Whiteboards/ folder — DiagramCatalog's DiagramEntry, one folder over.
internal readonly record struct WhiteboardEntry(string Title, string FilePath, string HomeLabel);

// W-2/AC-843: where a board lives, keyed off the same project-memory rows DiagramCatalog reads, one folder over
// (Whiteboards/ instead of Diagrams/) — JSON instead of Markdown, since a board's content is objects, not text.
internal static class WhiteboardCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static IReadOnlyList<WhiteboardEntry> List(IReadOnlyList<ProjectMemoryRow> rows)
    {
        var entries = new List<WhiteboardEntry>();
        foreach (var row in rows)
        {
            if (ProjectMemoryRef.TryParse(row.Reference, out _, out _))
            {
                continue;
            }

            var directory = Path.Combine(row.Reference, "Whiteboards");
            if (!Directory.Exists(directory))
            {
                continue;
            }

            var home = row.Label ?? row.Reference;
            foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
            {
                entries.Add(new WhiteboardEntry(_ReadTitle(file), file, home));
            }
        }

        return entries;
    }

    // Same folder-rows-only ceiling as DiagramCatalog.WritableHomes (AC-839) — reused rather than reimplemented,
    // it is already format-agnostic.
    public static IReadOnlyList<ProjectMemoryRow> WritableHomes(IReadOnlyList<ProjectMemoryRow> rows) =>
        DiagramCatalog.WritableHomes(rows);

    // First save: <memory>/Whiteboards/<slug>.json, the slug from the title once (AC-812's convention, one folder over).
    public static string Create(string homeReference, WhiteboardDocument document)
    {
        var directory = Path.Combine(homeReference, "Whiteboards");
        Directory.CreateDirectory(directory);

        var slug = PluginFolderName.Normalize(document.Title) is { Length: > 0 } normalized ? normalized : "whiteboard";
        var path = Path.Combine(directory, $"{slug}.json");
        for (var suffix = 2; File.Exists(path); suffix++)
        {
            path = Path.Combine(directory, $"{slug}-{suffix}.json");
        }

        Write(path, document);
        return path;
    }

    // The whole board per save, same "never a partial patch" rule as DiagramCatalog.Write. `expected` is the file
    // as this window last saw it — a mismatch is a conflict, not a silent overwrite (AC-812).
    public static void Write(string filePath, WhiteboardDocument document, string? expected = null)
    {
        if (expected is not null && File.Exists(filePath) && File.ReadAllText(filePath) != expected)
        {
            throw new IOException("the file was changed outside this window");
        }

        File.WriteAllText(filePath, Serialize(document));
    }

    // Every object comes back exactly as it was: strokes, placed shapes, pasted images and their positions and
    // sizes, and the agent badge flag (AC-854) — nothing here is lossy on the way back in.
    public static WhiteboardDocument Load(string filePath)
    {
        var dto = JsonSerializer.Deserialize<_BoardDto>(File.ReadAllText(filePath), JsonOptions) ?? new _BoardDto();
        var document = new WhiteboardDocument(id: filePath, title: dto.Title ?? Path.GetFileNameWithoutExtension(filePath), filePath: filePath);
        foreach (var entry in dto.Objects ?? [])
        {
            document.Add(entry.ToModel());
        }

        return document;
    }

    public static void Delete(string filePath) => File.Delete(filePath);

    internal static string Serialize(WhiteboardDocument document) =>
        JsonSerializer.Serialize(_BoardDto.From(document), JsonOptions);

    private static string _ReadTitle(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            using var json = JsonDocument.Parse(stream);
            return json.RootElement.TryGetProperty("title", out var title) && title.GetString() is { Length: > 0 } text
                ? text
                : Path.GetFileNameWithoutExtension(filePath);
        }
        catch (JsonException)
        {
            return Path.GetFileNameWithoutExtension(filePath);
        }
    }

    // Plain DTOs rather than JSON attributes on the model itself — WhiteboardObject stays the plain data
    // WhiteboardPoint's own comment asks for, readable without a serialization dependency in mind.
    private sealed class _BoardDto
    {
        public string? Title { get; set; }

        public List<_ObjectDto>? Objects { get; set; }

        public static _BoardDto From(WhiteboardDocument document) => new()
        {
            Title = document.Title,
            Objects = [.. document.Objects.Select(_ObjectDto.From)],
        };
    }

    private sealed class _ObjectDto
    {
        public Guid Id { get; set; }

        public WhiteboardObjectKind Kind { get; set; }

        // Freehand
        public List<WhiteboardPoint>? Points { get; set; }

        public double Thickness { get; set; }

        public bool IsMarker { get; set; }

        // Placed
        public PlacedShapeKind ShapeKind { get; set; }

        public double X { get; set; }

        public double Y { get; set; }

        public double Width { get; set; }

        public double Height { get; set; }

        public string? Text { get; set; }

        public byte[]? ImageData { get; set; }

        public bool IsPastedScreenshot { get; set; }

        public bool PlacedByAgent { get; set; }

        // W-6/AC-851: which pasted image (by id) this object is anchored to, or null when it stands free.
        public Guid? ParentImageId { get; set; }

        public static _ObjectDto From(WhiteboardObject obj) => obj switch
        {
            FreehandStroke stroke => new _ObjectDto
            {
                Id = stroke.Id,
                Kind = WhiteboardObjectKind.Freehand,
                Points = stroke.Points,
                Thickness = stroke.Thickness,
                IsMarker = stroke.IsMarker,
                ParentImageId = stroke.ParentImageId,
            },
            PlacedObject placed => new _ObjectDto
            {
                Id = placed.Id,
                Kind = WhiteboardObjectKind.Placed,
                ShapeKind = placed.ShapeKind,
                X = placed.X,
                Y = placed.Y,
                Width = placed.Width,
                Height = placed.Height,
                Text = placed.Text,
                ImageData = placed.ImageData,
                IsPastedScreenshot = placed.IsPastedScreenshot,
                PlacedByAgent = placed.PlacedByAgent,
                ParentImageId = placed.ParentImageId,
            },
            _ => throw new NotSupportedException($"Unknown whiteboard object type {obj.GetType()}"),
        };

        public WhiteboardObject ToModel() => Kind switch
        {
            WhiteboardObjectKind.Freehand => new FreehandStroke { Id = Id, Points = Points ?? [], Thickness = Thickness, IsMarker = IsMarker, ParentImageId = ParentImageId },
            WhiteboardObjectKind.Placed => new PlacedObject
            {
                Id = Id,
                ShapeKind = ShapeKind,
                X = X,
                Y = Y,
                Width = Width,
                Height = Height,
                Text = Text,
                ImageData = ImageData,
                IsPastedScreenshot = IsPastedScreenshot,
                PlacedByAgent = PlacedByAgent,
                ParentImageId = ParentImageId,
            },
            _ => throw new NotSupportedException($"Unknown whiteboard object kind {Kind}"),
        };
    }
}
