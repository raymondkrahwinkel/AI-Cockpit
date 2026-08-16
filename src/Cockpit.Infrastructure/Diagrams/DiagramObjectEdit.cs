namespace Cockpit.Infrastructure.Diagrams;

// One per-object edit's outcome: the new source plus a readable one-line summary of what changed (the activity
// strip's line, AC-848), or a refusal the agent can act on. `Text` is null exactly when `Refusal` is not.
internal readonly record struct DiagramEdit(string? Text, string Summary, string? Refusal)
{
    public static DiagramEdit Change(string text, string summary) => new(text, summary, null);

    public static DiagramEdit Refuse(string reason) => new(null, "", reason);
}

// AC-852: editing a diagram one object at a time. Line surgery rather than a parse-and-re-emit round trip — the
// source stays the operator's own text, and a call only rewrites the lines naming its object.
// ponytail: flowchart/graph only, and a chain ('A --> B --> C') is refused rather than split — edit_diagram does both.
internal static class DiagramObjectEdit
{
    private const string DefaultHeader = "flowchart TD";
    private const string Indent = "    ";
    private const string Openers = "([{>";
    private const string Closers = ")]}";

    // Lines that carry diagram structure rather than an object, and whose words must never be read as node ids.
    private static readonly string[] Keywords =
        ["flowchart", "graph", "subgraph", "end", "direction", "classDef", "class", "style", "linkStyle", "click", "---"];

    public static DiagramEdit AddNode(string source, string id, string label)
    {
        if (Reject(source, id) is { } refusal)
        {
            return DiagramEdit.Refuse(refusal);
        }

        if (Has(source, id))
        {
            return DiagramEdit.Refuse($"Node \"{id}\" is already in this diagram — rename_node changes its label.");
        }

        var text = Clean(label);
        return DiagramEdit.Change(Append(source, $"{Indent}{id}[\"{text}\"]"), $"added node {id} \"{text}\"");
    }

    // Renames the label, never the id: the id is what every connection is written in terms of, so changing it
    // would rewrite lines this call did not name.
    public static DiagramEdit RenameNode(string source, string id, string label)
    {
        if (Reject(source, id) is { } refusal)
        {
            return DiagramEdit.Refuse(refusal);
        }

        var lines = Lines(source);
        for (var i = 0; i < lines.Length; i++)
        {
            if (Occurrence(lines[i], id) is not { } at)
            {
                continue;
            }

            var text = Clean(label);
            lines[i] = Relabel(lines[i], at, id, text);
            return DiagramEdit.Change(string.Join("\n", lines), $"renamed node {id} to \"{text}\"");
        }

        return DiagramEdit.Refuse(NoSuchNode(id));
    }

    // A connection whose node is gone would resurrect that node on the next render, so the node's own
    // connections go with it — nothing else does.
    public static DiagramEdit RemoveNode(string source, string id)
    {
        if (Reject(source, id) is { } refusal)
        {
            return DiagramEdit.Refuse(refusal);
        }

        var kept = new List<string>();
        var connections = 0;
        var found = false;
        foreach (var line in Lines(source))
        {
            var mentions = Occurrence(line, id) is not null;
            var connection = FidelityCheck.ReadConnection(line);
            if (mentions && connection is { Connectors: > 1 })
            {
                return DiagramEdit.Refuse(Chain(line));
            }

            if (mentions && connection is not null)
            {
                connections++;
                found = true;
                continue;
            }

            if (mentions)
            {
                found = true;
                continue;
            }

            kept.Add(line);
        }

        if (!found)
        {
            return DiagramEdit.Refuse(NoSuchNode(id));
        }

        var summary = connections == 0
            ? $"removed node {id}"
            : $"removed node {id} and its {connections} connection{(connections == 1 ? "" : "s")}";
        return DiagramEdit.Change(string.Join("\n", kept), summary);
    }

    public static DiagramEdit Connect(string source, string from, string to, string? label)
    {
        if ((Reject(source, from) ?? InvalidId(to)) is { } refusal)
        {
            return DiagramEdit.Refuse(refusal);
        }

        if (Connections(source).Any(c => c.From == from && c.To == to))
        {
            return DiagramEdit.Refuse($"{from} -> {to} is already connected.");
        }

        var text = string.IsNullOrWhiteSpace(label) ? null : Clean(label);
        var line = text is null ? $"{Indent}{from} --> {to}" : $"{Indent}{from} -->|\"{text}\"| {to}";
        var summary = text is null ? $"connected {from} -> {to}" : $"connected {from} -> {to} \"{text}\"";
        return DiagramEdit.Change(Append(source, line), summary);
    }

    public static DiagramEdit Disconnect(string source, string from, string to)
    {
        if ((Reject(source, from) ?? InvalidId(to)) is { } refusal)
        {
            return DiagramEdit.Refuse(refusal);
        }

        var kept = new List<string>();
        var removed = 0;
        foreach (var line in Lines(source))
        {
            if (FidelityCheck.ReadConnection(line) is not { } connection || connection.From != from || connection.To != to)
            {
                kept.Add(line);
                continue;
            }

            if (connection.Connectors > 1)
            {
                return DiagramEdit.Refuse(Chain(line));
            }

            removed++;
        }

        return removed == 0
            ? DiagramEdit.Refuse($"There is no {from} -> {to} connection in this diagram.")
            : DiagramEdit.Change(string.Join("\n", kept), $"disconnected {from} -> {to}");
    }

    private static string? Reject(string source, string id) => Unsupported(source) ?? InvalidId(id);

    // Ids go into the source verbatim, so they are held to what a Mermaid id may safely be rather than escaped
    // after the fact.
    private static string? InvalidId(string id) =>
        !string.IsNullOrEmpty(id) && id.All(IsIdChar)
            ? null
            : "A node id must be one word of letters, digits or underscores — the label carries the wording.";

    // Every other diagram type writes its objects with a different grammar; guessing at one corrupts the source
    // instead of failing, so only the family whose lines this class actually understands is edited per object.
    private static string? Unsupported(string source)
    {
        var header = Body(Lines(source)).FirstOrDefault();
        if (header is null)
        {
            return null; // Nothing on the surface yet — add_node writes the header itself.
        }

        var keyword = header.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
        return keyword.StartsWith("flowchart", StringComparison.Ordinal) || keyword.StartsWith("graph", StringComparison.Ordinal)
            ? null
            : "Editing one object at a time only works on flowchart/graph diagrams — use edit_diagram for this one.";
    }

    private static string NoSuchNode(string id) =>
        $"There is no node \"{id}\" in this diagram — read_diagram shows what is there.";

    private static string Chain(string line) =>
        $"\"{line.Trim()}\" writes several connections on one line — use edit_diagram to change a chain like that.";

    private static string[] Lines(string source) => source.ReplaceLineEndings("\n").Split('\n');

    // Normalized like every other path here (Lines/string.Join), so an appended line never leaves the source
    // half CRLF and half LF.
    private static string Append(string source, string line) =>
        string.IsNullOrWhiteSpace(source)
            ? $"{DefaultHeader}\n{line}"
            : $"{string.Join("\n", Lines(source)).TrimEnd()}\n{line}";

    // The lines past any YAML front matter and comments — the first of them is the header keyword.
    private static IEnumerable<string> Body(string[] lines)
    {
        var i = 0;
        while (i < lines.Length && lines[i].Trim().Length == 0)
        {
            i++;
        }

        if (i < lines.Length && lines[i].Trim() == "---")
        {
            i++;
            while (i < lines.Length && lines[i].Trim() != "---")
            {
                i++;
            }

            i++;
        }

        for (; i < lines.Length; i++)
        {
            var text = lines[i].Trim();
            if (text.Length > 0 && !text.StartsWith("%%", StringComparison.Ordinal))
            {
                yield return text;
            }
        }
    }

    private static bool Has(string source, string id) => Lines(source).Any(line => Occurrence(line, id) is not null);

    private static IEnumerable<FidelityCheck.ConnectionLine> Connections(string source) =>
        Lines(source).Select(FidelityCheck.ReadConnection).OfType<FidelityCheck.ConnectionLine>();

    // Where this line names the node itself, or null. Text inside a label ("A[\"Zip here\"]") or an edge label
    // is wording, not a reference, so only a match outside both counts.
    private static int? Occurrence(string line, string id)
    {
        if (IsStructural(line))
        {
            return null;
        }

        var depth = 0;
        var quoted = false;
        var piped = false;
        for (var i = 0; i < line.Length; i++)
        {
            var character = line[i];
            if (character == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (quoted)
            {
                continue;
            }

            if (character == '|')
            {
                piped = !piped;
                continue;
            }

            if (character is '[' or '(' or '{')
            {
                depth++;
                continue;
            }

            if (character is ']' or ')' or '}')
            {
                depth = Math.Max(0, depth - 1);
                continue;
            }

            if (depth > 0 || piped)
            {
                continue;
            }

            if (i + id.Length <= line.Length
                && string.CompareOrdinal(line, i, id, 0, id.Length) == 0
                && !IsIdChar(i == 0 ? ' ' : line[i - 1])
                && !IsIdChar(i + id.Length >= line.Length ? ' ' : line[i + id.Length]))
            {
                return i;
            }
        }

        return null;
    }

    private static bool IsStructural(string line)
    {
        var text = line.Trim();
        if (text.Length == 0 || text.StartsWith("%%", StringComparison.Ordinal))
        {
            return true;
        }

        var keyword = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
        return Keywords.Any(word => keyword.Equals(word, StringComparison.Ordinal));
    }

    // Rewrites the node's label in place, keeping whatever shape delimiters it already had ('([', '{{', '>' …).
    private static string Relabel(string line, int at, string id, string label)
    {
        var afterId = at + id.Length;
        if (ShapeEnd(line, afterId) is not { } end)
        {
            return string.Concat(line[..afterId], $"[\"{label}\"]", line[afterId..]);
        }

        var block = line[afterId..end];
        var open = new string(block.TakeWhile(c => Openers.Contains(c)).ToArray());
        var close = new string(block.Reverse().TakeWhile(c => Closers.Contains(c)).Reverse().ToArray());
        return string.Concat(line[..afterId], open, $"\"{label}\"", close, line[end..]);
    }

    // The end of the shape block opening at `open`, counting nesting so a label's own bracket ("A[Text (x)]")
    // does not close it early. Null when there is no shape there at all.
    private static int? ShapeEnd(string line, int open)
    {
        if (open >= line.Length || !Openers.Contains(line[open]))
        {
            return null;
        }

        var depth = 0;
        var quoted = false;
        for (var i = open; i < line.Length; i++)
        {
            var character = line[i];
            if (character == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (quoted)
            {
                continue;
            }

            if (character is '[' or '(' or '{')
            {
                depth++;
            }
            else if (character is ']' or ')' or '}' && --depth <= 0)
            {
                return i + 1;
            }
        }

        return null;
    }

    // Labels are written quoted, so the only characters that could break out of one are the quote itself and
    // anything that would end the line.
    private static string Clean(string label) =>
        new(label.Select(character => character switch
        {
            '"' => '\'',
            _ => char.IsControl(character) ? ' ' : character,
        }).ToArray());

    private static bool IsIdChar(char character) => char.IsLetterOrDigit(character) || character == '_';
}
