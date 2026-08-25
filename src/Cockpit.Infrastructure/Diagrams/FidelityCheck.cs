using System.Text.RegularExpressions;
using System.Xml.Linq;
using Cockpit.Core.Abstractions.Diagrams;

namespace Cockpit.Infrastructure.Diagrams;

// Counts connections and notes on both sides of the render (AC-808) via a dumb line scan, not a second
// parser, since comparing against the engine's own model is useless when that model lost the construct.
// Dropped-line naming is best-effort (kept only when it matches the count); only verified diagram types are checked.
internal static partial class FidelityCheck
{
    // The diagram types whose connection and note markers were read off Mermaider 0.12.2's own output.
    // Matched on the header keyword, which also covers the '-v2' spellings.
    private static readonly string[] VerifiedTypes =
        ["flowchart", "graph", "stateDiagram", "sequenceDiagram", "classDiagram", "erDiagram"];

    // '[*]' is the state-diagram start/end pseudo-node; Mermaider renders it under a generated id
    // (_start, _start2, _end), so it can only be matched positionally.
    private const string Wildcard = "*";

    // Characters that decorate a connector's core run of -.= : arrowheads, ER cardinality, class-diagram
    // relation ends. 'o' and 'x' are deliberately absent — they are also ordinary identifier letters, so
    // 'Echo-->Bar' would otherwise lose its tail. They are handled at the id boundary instead.
    private const string Decoration = "<>|{}*()";

    // Shape and label openers: everything from here on is presentation, not the node's id.
    private static readonly char[] IdEnd = ['[', '(', '{', '<', '>', ':', '\\', '"', '/', '|'];

    public static DiagramFidelity Check(string source, string svg)
    {
        var lines = source.ReplaceLineEndings("\n").Split('\n');
        var body = FirstBodyLine(lines);
        if (!IsVerifiedType(lines, body))
        {
            return new DiagramFidelity([]);
        }

        var (edges, notes) = ScanSource(lines, body);
        var (drawnEdges, drawnNotes) = ScanSvg(svg);

        var findings = new List<string>();

        var missingEdges = edges.Count - drawnEdges.Count;
        if (missingEdges > 0)
        {
            var unmatched = Unmatched(edges, drawnEdges);
            findings.Add(unmatched.Count == missingEdges
                ? $"{missingEdges} of {edges.Count} connections in the source were not drawn: "
                  + string.Join("; ", unmatched.Select(e => $"line {e.Line} \"{e.Text}\""))
                : $"{missingEdges} of {edges.Count} connections in the source were not drawn "
                  + "(which ones could not be determined).");
        }

        if (notes > drawnNotes)
        {
            findings.Add($"{notes - drawnNotes} of {notes} notes in the source were not drawn.");
        }

        return new DiagramFidelity(findings);
    }

    private sealed record SourceEdge(int Line, string Text, string From, string To);

    // One line's connection, for the per-object edit tools (AC-852): the same connector scan, plus how many
    // connectors the line holds — a chain ('A --> B --> C') is one line they must not edit in place.
    internal readonly record struct ConnectionLine(string From, string To, int Connectors);

    internal static ConnectionLine? ReadConnection(string line)
    {
        var text = line.Trim();
        if (ReadEdge(text, 0) is not { } edge)
        {
            return null;
        }

        var scrubbed = EdgeLabel().Replace(QuotedText().Replace(text, "\"\""), " ");
        return new ConnectionLine(edge.From, edge.To, Connectors(scrubbed).Count);
    }

    // The first line past any YAML front matter. The '---' that opens it is also a flowchart link, so it
    // only counts as front matter when nothing but blank lines precedes it.
    private static int FirstBodyLine(string[] lines)
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

        return i;
    }

    private static bool IsVerifiedType(string[] lines, int body)
    {
        for (var i = body; i < lines.Length; i++)
        {
            var text = lines[i].Trim();
            if (text.Length == 0 || text.StartsWith("%%", StringComparison.Ordinal))
            {
                continue;
            }

            var keyword = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
            return VerifiedTypes.Any(t => keyword.StartsWith(t, StringComparison.Ordinal));
        }

        return false;
    }

    private static (List<SourceEdge> Edges, int Notes) ScanSource(string[] lines, int body)
    {
        var edges = new List<SourceEdge>();
        var notes = 0;

        for (var i = body; i < lines.Length; i++)
        {
            var text = lines[i].Trim();
            if (text.Length == 0 || text.StartsWith("%%", StringComparison.Ordinal))
            {
                continue;
            }

            if (IsNoteOpener(text))
            {
                notes++;

                // A note without ':' opens a free-text block running until 'end note'; its prose body may
                // contain an arrow, so it must not be scanned. Only skip when that terminator exists — a
                // classDiagram's note has none, and swallowing the rest of the file would drop connections.
                if (!text.Contains(':', StringComparison.Ordinal) && EndOfNote(lines, i) is { } close)
                {
                    i = close;
                }

                continue;
            }

            // A brace block that is not a composite state holds members (class) or attributes (ER), never
            // connections, and its lines can read like one. A composite state's body is scanned normally.
            if (text.EndsWith('{') && !text.StartsWith("state ", StringComparison.Ordinal))
            {
                var depth = 1;
                while (depth > 0 && i + 1 < lines.Length)
                {
                    i++;
                    var inner = lines[i].Trim();
                    if (inner.EndsWith('{'))
                    {
                        depth++;
                    }
                    else if (inner.StartsWith('}'))
                    {
                        depth--;
                    }
                }

                continue;
            }

            if (ReadEdge(text, i + 1) is { } edge)
            {
                edges.Add(edge);
            }
        }

        return (edges, notes);
    }

    // The line closing the note block opened on the given line, or null when it has no terminator.
    private static int? EndOfNote(string[] lines, int opener)
    {
        for (var i = opener + 1; i < lines.Length; i++)
        {
            var text = lines[i].Trim();
            if (text.Equals("end note", StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }

            // Another note starts before this one closed, so this one never had a terminator.
            if (IsNoteOpener(text))
            {
                return null;
            }
        }

        return null;
    }

    private static bool IsNoteOpener(string text) =>
        text.StartsWith("note", StringComparison.OrdinalIgnoreCase)
        && (text.Length == 4 || char.IsWhiteSpace(text[4]));

    private static SourceEdge? ReadEdge(string text, int line)
    {
        // Quoted labels may hold anything, including arrows; edge labels sit between the connector and the
        // target and would otherwise be read as the target. Both go before the scan.
        var scrubbed = QuotedText().Replace(text, "\"\"");
        scrubbed = EdgeLabel().Replace(scrubbed, " ");

        var connectors = Connectors(scrubbed);
        if (connectors.Count == 0)
        {
            return null;
        }

        var from = LeftId(scrubbed[..connectors[0].Start]);
        var to = RightId(scrubbed[connectors[^1].End..]);
        return from is null || to is null ? null : new SourceEdge(line, text, from, to);
    }

    // A connector is a run of '-', '.' or '=' plus whatever decoration hangs off either end. Two characters
    // minimum, so a hyphen inside an identifier or a date is not one.
    private static List<(int Start, int End)> Connectors(string text)
    {
        var found = new List<(int, int)>();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('-' or '.' or '='))
            {
                continue;
            }

            var core = i;
            while (core < text.Length && text[core] is '-' or '.' or '=')
            {
                core++;
            }

            var start = i;
            while (start > 0 && Decoration.Contains(text[start - 1], StringComparison.Ordinal))
            {
                start--;
            }

            var end = core;
            while (end < text.Length && Decoration.Contains(text[end], StringComparison.Ordinal))
            {
                end++;
            }

            if (end - start >= 2)
            {
                found.Add((start, end));
            }

            i = end - 1;
        }

        return found;
    }

    private static string? LeftId(string left)
    {
        // 'o' and 'x' are connector ends in 'A --o B' but letters in 'Echo'. Only drop them when doing so
        // lands on a whitespace boundary, which an identifier's own tail never does.
        var end = left.Length;
        while (end > 0 && (Decoration.Contains(left[end - 1], StringComparison.Ordinal) || left[end - 1] is 'o' or 'x'))
        {
            end--;
        }

        if (end < left.Length && end > 0 && !char.IsWhiteSpace(left[end - 1]))
        {
            end = left.Length;
        }

        var token = left[..end].TrimEnd().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return NodeId(token);
    }

    private static string? RightId(string right)
    {
        var start = 0;
        while (start < right.Length && (Decoration.Contains(right[start], StringComparison.Ordinal) || right[start] is 'o' or 'x'))
        {
            start++;
        }

        if (start > 0 && (start == right.Length || !char.IsWhiteSpace(right[start])))
        {
            start = 0;
        }

        var token = right[start..].TrimStart().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return NodeId(token);
    }

    private static string? NodeId(string? token)
    {
        if (token is null)
        {
            return null;
        }

        if (token.StartsWith("[*]", StringComparison.Ordinal))
        {
            return Wildcard;
        }

        var cut = token.IndexOfAny(IdEnd);
        var id = cut >= 0 ? token[..cut] : token;
        return id.Length == 0 ? null : id;
    }

    private static (List<(string From, string To)> Edges, int Notes) ScanSvg(string svg)
    {
        var edges = new List<(string, string)>();
        var notes = 0;

        foreach (var element in XDocument.Parse(svg).Descendants())
        {
            var cls = element.Attribute("class")?.Value;

            // The label of a connection repeats its data-from/data-to; counting both doubles every labelled
            // connection and hides a real drop.
            if (cls is not null && cls.EndsWith("-label", StringComparison.Ordinal))
            {
                continue;
            }

            if (cls == "note")
            {
                notes++;
            }

            var from = element.Attribute("data-from")?.Value ?? element.Attribute("data-entity1")?.Value;
            var to = element.Attribute("data-to")?.Value ?? element.Attribute("data-entity2")?.Value;
            if (from is not null && to is not null)
            {
                edges.Add((from, to));
            }
        }

        return (edges, notes);
    }

    private static List<SourceEdge> Unmatched(List<SourceEdge> source, List<(string From, string To)> drawn)
    {
        var pool = new List<(string From, string To)>(drawn);
        var missing = new List<SourceEdge>();

        // Named pairs claim their match first, so a '[*]' wildcard cannot consume a connection that a
        // named pair still needed.
        foreach (var edge in source.OrderBy(e => e.From == Wildcard || e.To == Wildcard))
        {
            var hit = pool.FindIndex(d => Same(edge, d));
            if (hit >= 0)
            {
                pool.RemoveAt(hit);
            }
            else
            {
                missing.Add(edge);
            }
        }

        return missing.OrderBy(e => e.Line).ToList();
    }

    // Direction is not what is being checked here — class-diagram relations are written tail-first — so a
    // pair matches either way round.
    private static bool Same(SourceEdge edge, (string From, string To) drawn) =>
        (Same(edge.From, drawn.From) && Same(edge.To, drawn.To))
        || (Same(edge.From, drawn.To) && Same(edge.To, drawn.From));

    private static bool Same(string source, string drawn) =>
        source == Wildcard || string.Equals(source, drawn, StringComparison.Ordinal);

    [GeneratedRegex("\"[^\"]*\"")]
    private static partial Regex QuotedText();

    // An edge label, '|like this|', but only where it hangs off a connector — the same character is ER
    // cardinality ('||--o{'), which is part of the connector rather than a label.
    [GeneratedRegex(@"(?<=[-.=>])\|[^|]*\|")]
    private static partial Regex EdgeLabel();
}
