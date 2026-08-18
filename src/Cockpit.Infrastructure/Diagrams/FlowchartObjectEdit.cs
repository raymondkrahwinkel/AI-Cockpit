using System.Text.RegularExpressions;
using Cockpit.Core.Abstractions.Diagrams;

namespace Cockpit.Infrastructure.Diagrams;

// The per-object grammar for flowchart/graph sources (AC-852), reached through DiagramObjectEdit once the header
// keyword says this is the dialect. A node is one line, so every call here is single-line surgery.
// ponytail: a chain ('A --> B --> C') is refused rather than split — edit_diagram does that.
internal static partial class FlowchartObjectEdit
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

    // Rewrites the label on an existing connection, keeping its connector style ('-->', '-.->', '==>' …) exactly as
    // written. Ids never contain '-', '.' or '=' (InvalidId), so the leftmost connector run in the line is always
    // the real one, never something inside a label.
    public static DiagramEdit RelabelConnection(string source, string from, string to, string? label)
    {
        if ((DiagramObjectEdit.InvalidId(from, "node") ?? DiagramObjectEdit.InvalidId(to, "node")) is { } refusal)
        {
            return DiagramEdit.Refuse(refusal);
        }

        var lines = DiagramObjectEdit.Lines(source);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (FidelityCheck.ReadConnection(line) is not { } connection || connection.From != from || connection.To != to)
            {
                continue;
            }

            if (connection.Connectors > 1)
            {
                return DiagramEdit.Refuse(Chain(line));
            }

            var connector = ConnectorCore().Match(line);
            if (!connector.Success)
            {
                return DiagramEdit.Refuse($"\"{line.Trim()}\" does not have a connector this can relabel — use edit_diagram for this one.");
            }

            var after = connector.Index + connector.Length;
            var tail = line[after..];
            var existing = EdgeLabelPipe().Match(tail);
            var rest = existing.Success ? tail[existing.Length..] : tail;
            var text = string.IsNullOrWhiteSpace(label) ? null : DiagramObjectEdit.Clean(label);
            var newTail = text is null ? rest : $"|\"{text}\"|{rest}";
            var summary = text is null
                ? $"cleared the label on connection {from} -> {to}"
                : $"labeled connection {from} -> {to} \"{text}\"";
            lines[i] = line[..after] + newTail;
            return DiagramEdit.Change(string.Join("\n", lines), summary);
        }

        return DiagramEdit.Refuse($"There is no {from} -> {to} connection in this diagram.");
    }

    public static DiagramEdit SetNodeShape(string source, string id, DiagramNodeShape shape)
    {
        if (DiagramObjectEdit.InvalidId(id, "node") is { } refusal)
        {
            return DiagramEdit.Refuse(refusal);
        }

        var (open, close) = Delimiters(shape);
        return Reshape(source, id, open, close, $"changed the shape of node {id} to {ShapeName(shape)}");
    }

    // SetNodeShape's own inverse (AC-853): restores the exact delimiters an earlier line had, whatever they were —
    // not limited to the five named shapes, so a hand-written shape survives too. Keeps whatever label is on the
    // line now, symmetric with RenameNode's inverse keeping whatever shape is on the line now.
    public static DiagramEdit RestoreNodeShape(string source, string id, string oldLine)
    {
        if (DiagramObjectEdit.InvalidId(id, "node") is { } refusal)
        {
            return DiagramEdit.Refuse(refusal);
        }

        var (open, close) = ShapeDelimitersAt(oldLine, id) ?? ("", "");
        return Reshape(source, id, open, close, "restored shape");
    }

    private static (string Open, string Close) Delimiters(DiagramNodeShape shape) => shape switch
    {
        DiagramNodeShape.Rectangle => ("[", "]"),
        DiagramNodeShape.Rounded => ("(", ")"),
        DiagramNodeShape.Diamond => ("{", "}"),
        DiagramNodeShape.Stadium => ("([", "])"),
        DiagramNodeShape.Subroutine => ("[[", "]]"),
        _ => ("[", "]"),
    };

    private static string ShapeName(DiagramNodeShape shape) => shape switch
    {
        DiagramNodeShape.Rectangle => "rectangle",
        DiagramNodeShape.Rounded => "rounded",
        DiagramNodeShape.Diamond => "diamond",
        DiagramNodeShape.Stadium => "stadium",
        DiagramNodeShape.Subroutine => "subroutine",
        _ => "rectangle",
    };

    // Replaces whatever shape delimiters sit at `id` (or nothing, for an implicit node) with `open`/`close`,
    // keeping the current label — or, when `open` is empty, drops the shape entirely and leaves the bare id, which
    // is how RestoreNodeShape undoes a materialization SetNodeShape made on an implicit node.
    private static DiagramEdit Reshape(string source, string id, string open, string close, string summary)
    {
        var lines = DiagramObjectEdit.Lines(source);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (Occurrence(line, id) is not { } at)
            {
                continue;
            }

            var afterId = at + id.Length;
            var end = ShapeEnd(line, afterId);
            var label = end is { } shapeEnd ? LabelBetween(line, afterId, shapeEnd) : id;
            var block = open.Length == 0 ? "" : $"{open}\"{label}\"{close}";
            var tailStart = end ?? afterId;
            lines[i] = string.Concat(line[..afterId], block, line[tailStart..]);
            return DiagramEdit.Change(string.Join("\n", lines), summary);
        }

        return DiagramEdit.Refuse(NoSuchNode(id));
    }

    // The literal open/close delimiters `id` was drawn with on `line`, or null for an implicit node (bare id, no
    // shape block at all) — as opposed to Delimiters(shape), which only knows the five named shapes.
    private static (string Open, string Close)? ShapeDelimitersAt(string line, string id)
    {
        if (Occurrence(line, id) is not { } at)
        {
            return null;
        }

        var afterId = at + id.Length;
        if (ShapeEnd(line, afterId) is not { } end)
        {
            return null;
        }

        var block = line[afterId..end];
        var open = new string(block.TakeWhile(c => Openers.Contains(c)).ToArray());
        var close = new string(block.Reverse().TakeWhile(c => Closers.Contains(c)).Reverse().ToArray());
        return (open, close);
    }

    // The label text inside a shape block, quotes stripped — mirrors Relabel's own open/close trim so both read
    // the same block the same way.
    private static string LabelBetween(string line, int afterId, int end)
    {
        var block = line[afterId..end];
        var openLength = block.TakeWhile(c => Openers.Contains(c)).Count();
        var closeLength = block.Reverse().TakeWhile(c => Closers.Contains(c)).Count();
        var inner = block[openLength..^closeLength];
        return inner.Length >= 2 && inner[0] == '"' && inner[^1] == '"' ? inner[1..^1] : inner;
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

    // A connector's core run, with at most one arrowhead on either side — '-->', '<-->', '-.->', '==>', '--x' …
    [GeneratedRegex(@"[<ox]?[-.=]{2,}[>ox]?")]
    private static partial Regex ConnectorCore();

    // An edge label immediately after the connector, '|"text"|' — same shape Connect() writes.
    [GeneratedRegex(@"^\|[^|]*\|")]
    private static partial Regex EdgeLabelPipe();
}
