namespace Cockpit.Infrastructure.Diagrams;

// The per-object grammar for flowchart/graph sources (AC-852), reached through DiagramObjectEdit once the header
// keyword says this is the dialect. A node is one line, so every call here is single-line surgery.
// ponytail: a chain ('A --> B --> C') is refused rather than split — edit_diagram does that.
internal static class FlowchartObjectEdit
{
    private const string DefaultHeader = "flowchart TD";
    private const string Openers = "([{>";
    private const string Closers = ")]}";

    // Lines that carry diagram structure rather than an object, and whose words must never be read as node ids.
    private static readonly string[] Keywords =
        ["flowchart", "graph", "subgraph", "end", "direction", "classDef", "class", "style", "linkStyle", "click", "---"];

    public static DiagramEdit AddNode(string source, string id, string label)
    {
        if (DiagramObjectEdit.InvalidId(id, "node") is { } refusal)
        {
            return DiagramEdit.Refuse(refusal);
        }

        if (Has(source, id))
        {
            return DiagramEdit.Refuse($"Node \"{id}\" is already in this diagram — rename_node changes its label.");
        }

        var text = DiagramObjectEdit.Clean(label);
        return DiagramEdit.Change(
            DiagramObjectEdit.Append(source, DiagramObjectEdit.Indented($"{id}[\"{text}\"]"), DefaultHeader),
            $"added node {id} \"{text}\"");
    }

    // Renames the label, never the id: the id is what every connection is written in terms of, so changing it
    // would rewrite lines this call did not name.
    public static DiagramEdit RenameNode(string source, string id, string label)
    {
        if (DiagramObjectEdit.InvalidId(id, "node") is { } refusal)
        {
            return DiagramEdit.Refuse(refusal);
        }

        var lines = DiagramObjectEdit.Lines(source);
        for (var i = 0; i < lines.Length; i++)
        {
            if (Occurrence(lines[i], id) is not { } at)
            {
                continue;
            }

            var text = DiagramObjectEdit.Clean(label);
            lines[i] = Relabel(lines[i], at, id, text);
            return DiagramEdit.Change(string.Join("\n", lines), $"renamed node {id} to \"{text}\"");
        }

        return DiagramEdit.Refuse(NoSuchNode(id));
    }

    // A connection whose node is gone would resurrect that node on the next render, so the node's own
    // connections go with it — nothing else does.
    public static DiagramEdit RemoveNode(string source, string id)
    {
        if (DiagramObjectEdit.InvalidId(id, "node") is { } refusal)
        {
            return DiagramEdit.Refuse(refusal);
        }

        var kept = new List<string>();
        var connections = 0;
        var found = false;
        foreach (var line in DiagramObjectEdit.Lines(source))
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
        if ((DiagramObjectEdit.InvalidId(from, "node") ?? DiagramObjectEdit.InvalidId(to, "node")) is { } refusal)
        {
            return DiagramEdit.Refuse(refusal);
        }

        if (Connections(source).Any(c => c.From == from && c.To == to))
        {
            return DiagramEdit.Refuse($"{from} -> {to} is already connected.");
        }

        var text = string.IsNullOrWhiteSpace(label) ? null : DiagramObjectEdit.Clean(label);
        var line = text is null ? $"{from} --> {to}" : $"{from} -->|\"{text}\"| {to}";
        var summary = text is null ? $"connected {from} -> {to}" : $"connected {from} -> {to} \"{text}\"";
        return DiagramEdit.Change(DiagramObjectEdit.Append(source, DiagramObjectEdit.Indented(line), DefaultHeader), summary);
    }

    public static DiagramEdit Disconnect(string source, string from, string to)
    {
        if ((DiagramObjectEdit.InvalidId(from, "node") ?? DiagramObjectEdit.InvalidId(to, "node")) is { } refusal)
        {
            return DiagramEdit.Refuse(refusal);
        }

        var kept = new List<string>();
        var removed = 0;
        foreach (var line in DiagramObjectEdit.Lines(source))
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

    private static string NoSuchNode(string id) =>
        $"There is no node \"{id}\" in this diagram — read_diagram shows what is there.";

    private static string Chain(string line) =>
        $"\"{line.Trim()}\" writes several connections on one line — use edit_diagram to change a chain like that.";

    private static bool Has(string source, string id) =>
        DiagramObjectEdit.Lines(source).Any(line => Occurrence(line, id) is not null);

    private static IEnumerable<FidelityCheck.ConnectionLine> Connections(string source) =>
        DiagramObjectEdit.Lines(source).Select(FidelityCheck.ReadConnection).OfType<FidelityCheck.ConnectionLine>();

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
                && !DiagramObjectEdit.IsIdChar(i == 0 ? ' ' : line[i - 1])
                && !DiagramObjectEdit.IsIdChar(i + id.Length >= line.Length ? ' ' : line[i + id.Length]))
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
}
