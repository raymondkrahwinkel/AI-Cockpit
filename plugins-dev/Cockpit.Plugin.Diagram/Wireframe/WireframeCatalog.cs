using Cockpit.Core.Plugins;
using Cockpit.Core.Projects;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Plugin.Diagram.Wireframe;

// One wireframe found under a Memory row's Wireframes/ folder (AC-812's file convention) — DiagramCatalog's
// DiagramEntry, one folder over.
internal readonly record struct WireframeEntry(string Title, string FilePath, string HomeLabel, string WireframeText);

// AC-874/WF-4: lists AC-812's <memory>/Wireframes/<slug>.md files across every Memory row — DiagramCatalog's
// counterpart, Markdown since a wireframe's source is text, like a diagram's. Depot-scheme rows stay unbrowsable,
// same known ceiling DiagramCatalog already names.
internal static class WireframeCatalog
{
    public static IReadOnlyList<WireframeEntry> List(IReadOnlyList<ProjectMemoryRow> rows)
    {
        var entries = new List<WireframeEntry>();
        foreach (var row in rows)
        {
            if (ProjectMemoryRef.TryParse(row.Reference, out _, out _))
            {
                continue;
            }

            var directory = Path.Combine(row.Reference, "Wireframes");
            if (!Directory.Exists(directory))
            {
                continue;
            }

            var home = row.Label ?? row.Reference;
            foreach (var file in Directory.EnumerateFiles(directory, "*.md"))
            {
                entries.Add(new WireframeEntry(_ReadTitle(file), file, home, _ReadWireframe(file)));
            }
        }

        return entries;
    }

    // Same folder-rows-only ceiling as DiagramCatalog.WritableHomes (AC-839) — reused rather than reimplemented,
    // it is already format-agnostic.
    public static IReadOnlyList<ProjectMemoryRow> WritableHomes(IReadOnlyList<ProjectMemoryRow> rows) =>
        DiagramCatalog.WritableHomes(rows);

    // First save: <memory>/Wireframes/<slug>.md, the slug taken from the title once. A slug already used in this
    // home gets -2, -3, … (AC-812) — a title is free to collide, a path is not.
    public static string Create(string homeReference, string title, string wireframeText)
    {
        var directory = Path.Combine(homeReference, "Wireframes");
        Directory.CreateDirectory(directory);

        var slug = PluginFolderName.Normalize(title) is { Length: > 0 } normalized ? normalized : "wireframe";
        var path = Path.Combine(directory, $"{slug}.md");
        for (var suffix = 2; File.Exists(path); suffix++)
        {
            path = Path.Combine(directory, $"{slug}-{suffix}.md");
        }

        Write(path, title, wireframeText);
        return path;
    }

    // The whole file per save, never a partial patch — the home's own history (git, Depot versions) keeps the diff
    // readable (AC-812). `expected` is the file as this window last saw it; no longer matching means the save does
    // not land at all (AC-812's "detect, never silently overwrite").
    public static void Write(string filePath, string title, string wireframeText, string? expected = null)
    {
        if (expected is not null && File.Exists(filePath) && File.ReadAllText(filePath) != expected)
        {
            throw new IOException("het bestand is buiten dit venster gewijzigd");
        }

        File.WriteAllText(filePath, $"# {title}\n\n```wireframe\n{wireframeText.TrimEnd()}\n```\n");
    }

    // Renaming only ever touches the H1 line — the file's path is its stable slug (AC-812) and never follows the
    // title. Splits/rejoins on '\n' rather than File.ReadAllLines/WriteAllLines so every other line's original
    // line ending survives untouched — a CRLF file must not turn into a whole-file diff for a one-line rename.
    public static void Rename(string filePath, string newTitle)
    {
        var lines = File.ReadAllText(filePath).Split('\n');
        var headingIndex = Array.FindIndex(lines, line => line.TrimEnd('\r').StartsWith("# ", StringComparison.Ordinal));
        var trailer = headingIndex >= 0 && lines[headingIndex].EndsWith('\r') ? "\r" : "";
        var heading = $"# {newTitle}{trailer}";
        if (headingIndex >= 0)
        {
            lines[headingIndex] = heading;
        }
        else
        {
            lines = [heading, .. lines];
        }

        File.WriteAllText(filePath, string.Join('\n', lines));
    }

    public static void Delete(string filePath) => File.Delete(filePath);

    private static string _ReadTitle(string filePath)
    {
        foreach (var line in File.ReadLines(filePath))
        {
            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                return line[2..].Trim();
            }
        }

        return Path.GetFileNameWithoutExtension(filePath);
    }

    private static string _ReadWireframe(string filePath)
    {
        var text = File.ReadAllText(filePath);
        var fenceStart = text.IndexOf("```wireframe", StringComparison.Ordinal);
        if (fenceStart < 0)
        {
            return string.Empty;
        }

        var bodyStart = text.IndexOf('\n', fenceStart);
        if (bodyStart < 0)
        {
            return string.Empty;
        }

        bodyStart++;
        var fenceEnd = text.IndexOf("```", bodyStart, StringComparison.Ordinal);
        return fenceEnd < 0 ? text[bodyStart..].Trim() : text[bodyStart..fenceEnd].Trim();
    }
}
