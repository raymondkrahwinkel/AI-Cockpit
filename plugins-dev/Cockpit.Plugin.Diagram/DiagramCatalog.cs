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
