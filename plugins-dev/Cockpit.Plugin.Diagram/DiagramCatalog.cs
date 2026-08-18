using Cockpit.Core.Plugins;
using Cockpit.Core.Projects;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Plugin.Diagram;

// One diagram found under a Memory row's Diagrams/ folder (AC-812's file convention).
internal readonly record struct DiagramEntry(string Title, string FilePath, string HomeLabel, string MermaidText);

// AC-826: lists AC-812's <memory>/Diagrams/<slug>.md files across every Memory row AC-827's read seam reports.
// Depot-scheme rows (`depot:...`) have no plugin-facing content-read seam yet, so only folder rows are browsable —
// ponytail: known ceiling, revisit if/when a Depot content-read seam lands.
internal static class DiagramCatalog
{
    public static IReadOnlyList<DiagramEntry> List(IReadOnlyList<ProjectMemoryRow> rows)
    {
        var entries = new List<DiagramEntry>();
        foreach (var row in rows)
        {
            if (ProjectMemoryRef.TryParse(row.Reference, out _, out _))
            {
                continue;
            }

            var directory = Path.Combine(row.Reference, "Diagrams");
            if (!Directory.Exists(directory))
            {
                continue;
            }

            var home = row.Label ?? row.Reference;
            foreach (var file in Directory.EnumerateFiles(directory, "*.md"))
            {
                entries.Add(new DiagramEntry(_ReadTitle(file), file, home, _ReadMermaid(file)));
            }
        }

        return entries;
    }

    // AC-839: the homes a diagram can actually be written to — folder rows only, same ceiling as List's read side.
    // Zero of them is the "refuse, point at the project editor" case; one saves without asking; more than one asks.
    public static IReadOnlyList<ProjectMemoryRow> WritableHomes(IReadOnlyList<ProjectMemoryRow> rows) =>
        [.. rows.Where(row => !ProjectMemoryRef.TryParse(row.Reference, out _, out _))];

    // First save: <memory>/Diagrams/<slug>.md, the slug taken from the title once. A slug already used in this
    // home gets -2, -3, … (AC-812) — a title is free to collide, a path is not.
    public static string Create(string homeReference, string title, string mermaidText)
    {
        var directory = Path.Combine(homeReference, "Diagrams");
        Directory.CreateDirectory(directory);

        var slug = PluginFolderName.Normalize(title) is { Length: > 0 } normalized ? normalized : "diagram";
        var path = Path.Combine(directory, $"{slug}.md");
        for (var suffix = 2; File.Exists(path); suffix++)
        {
            path = Path.Combine(directory, $"{slug}-{suffix}.md");
        }

        Write(path, title, mermaidText);
        return path;
    }

    // The whole file per save, never a partial patch — the home's own history (git, Depot versions) keeps the diff
    // readable (AC-812). `expected` is the file as this window last saw it; no longer matching means the save does
    // not land at all (AC-812's "detect, never silently overwrite" — resolving it through a diff is AC-825's).
    public static void Write(string filePath, string title, string mermaidText, string? expected = null)
    {
        if (expected is not null && File.Exists(filePath) && File.ReadAllText(filePath) != expected)
        {
            throw new IOException("the file was changed outside this window");
        }

        File.WriteAllText(filePath, $"# {title}\n\n```mermaid\n{mermaidText.TrimEnd()}\n```\n");
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

    private static string _ReadMermaid(string filePath)
    {
        var text = File.ReadAllText(filePath);
        var fenceStart = text.IndexOf("```mermaid", StringComparison.Ordinal);
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
