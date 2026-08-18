using Cockpit.Core.Abstractions.Diagrams;

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
// AC-899: which grammar does that surgery is chosen here, on the header keyword, one strategy per dialect.
internal static class DiagramObjectEdit
{
    private const string Indent = "    ";

    public static DiagramEditDialect DialectOf(string source) =>
        Header(source) switch
        {
            null => DiagramEditDialect.Flowchart, // Nothing on the surface yet — AddNode writes the header itself.
            var keyword when keyword.StartsWith("flowchart", StringComparison.Ordinal) => DiagramEditDialect.Flowchart,
            var keyword when keyword.StartsWith("graph", StringComparison.Ordinal) => DiagramEditDialect.Flowchart,
            var keyword when keyword.StartsWith("erDiagram", StringComparison.Ordinal) => DiagramEditDialect.Er,
            _ => DiagramEditDialect.Unsupported,
        };

    // The one entry point both origins take (AC-841 for the operator, AC-852 for the agent), so a kind that does
    // not belong to this surface's dialect is refused in one place rather than in each caller.
    public static DiagramEdit Apply(string source, DiagramHandEdit edit) => edit.Kind switch
    {
        DiagramHandEditKind.AddNode => AddNode(source, edit.Id, edit.Label ?? edit.Id),
        DiagramHandEditKind.RenameNode => RenameNode(source, edit.Id, edit.Label ?? edit.Id),
        DiagramHandEditKind.RemoveNode => RemoveNode(source, edit.Id),
        DiagramHandEditKind.Connect => Connect(source, edit.Id, edit.To ?? "", edit.Label),
        DiagramHandEditKind.Disconnect => Disconnect(source, edit.Id, edit.To ?? ""),
        DiagramHandEditKind.AddEntity => AddEntity(source, edit.Id),
        DiagramHandEditKind.RenameEntity => RenameEntity(source, edit.Id, edit.Label ?? edit.Id),
        DiagramHandEditKind.RemoveEntity => RemoveEntity(source, edit.Id),
        DiagramHandEditKind.SetAttribute => SetAttribute(source, edit.Id, edit.Attribute ?? "", edit.AttributeType ?? "", edit.AttributeKey),
        DiagramHandEditKind.RemoveAttribute => RemoveAttribute(source, edit.Id, edit.Attribute ?? ""),
        DiagramHandEditKind.Relate => Relate(source, edit.Id, edit.To ?? "", edit.FromCardinality, edit.ToCardinality, edit.Label),
        _ => Unrelate(source, edit.Id, edit.To ?? ""),
    };

    // ---- flowchart/graph (AC-852) ----

    public static DiagramEdit AddNode(string source, string id, string label) =>
        Wrong(source, DiagramEditDialect.Flowchart) ?? FlowchartObjectEdit.AddNode(source, id, label);

    public static DiagramEdit RenameNode(string source, string id, string label) =>
        Wrong(source, DiagramEditDialect.Flowchart) ?? FlowchartObjectEdit.RenameNode(source, id, label);

    public static DiagramEdit RemoveNode(string source, string id) =>
        Wrong(source, DiagramEditDialect.Flowchart) ?? FlowchartObjectEdit.RemoveNode(source, id);

    public static DiagramEdit Connect(string source, string from, string to, string? label) =>
        Wrong(source, DiagramEditDialect.Flowchart) ?? FlowchartObjectEdit.Connect(source, from, to, label);

    public static DiagramEdit Disconnect(string source, string from, string to) =>
        Wrong(source, DiagramEditDialect.Flowchart) ?? FlowchartObjectEdit.Disconnect(source, from, to);

    // ---- erDiagram (AC-899) ----

    public static DiagramEdit AddEntity(string source, string entity) =>
        Wrong(source, DiagramEditDialect.Er) ?? ErObjectEdit.AddEntity(source, entity);

    public static DiagramEdit RenameEntity(string source, string entity, string renamedTo) =>
        Wrong(source, DiagramEditDialect.Er) ?? ErObjectEdit.RenameEntity(source, entity, renamedTo);

    public static DiagramEdit RemoveEntity(string source, string entity) =>
        Wrong(source, DiagramEditDialect.Er) ?? ErObjectEdit.RemoveEntity(source, entity);

    public static DiagramEdit SetAttribute(string source, string entity, string attribute, string type, string? key) =>
        Wrong(source, DiagramEditDialect.Er) ?? ErObjectEdit.SetAttribute(source, entity, attribute, type, key);

    public static DiagramEdit RemoveAttribute(string source, string entity, string attribute) =>
        Wrong(source, DiagramEditDialect.Er) ?? ErObjectEdit.RemoveAttribute(source, entity, attribute);

    public static DiagramEdit Relate(string source, string from, string to, DiagramErCardinality? fromCardinality, DiagramErCardinality? toCardinality, string? label) =>
        Wrong(source, DiagramEditDialect.Er) ?? ErObjectEdit.Relate(source, from, to, fromCardinality, toCardinality, label);

    public static DiagramEdit Unrelate(string source, string from, string to) =>
        Wrong(source, DiagramEditDialect.Er) ?? ErObjectEdit.Unrelate(source, from, to);

    public static IReadOnlyList<DiagramErAttribute> Attributes(string source, string entity) =>
        DialectOf(source) == DiagramEditDialect.Er ? ErObjectEdit.Attributes(source, entity) : [];

    public static DiagramEdit InvertEr(string source, DiagramHandEditKind kind, string objectKey, IReadOnlyList<string> removedLines) =>
        Wrong(source, DiagramEditDialect.Er) ?? ErObjectEdit.Invert(source, kind, objectKey, removedLines);

    // The header keyword as it stands in the source, for a message that has to name this diagram's type back to
    // the operator. Empty for a surface with nothing on it yet.
    public static string Keyword(string source) => Header(source) ?? "";

    // ---- shared by both strategies ----

    // Ids go into the source verbatim, so they are held to what a Mermaid id may safely be rather than escaped
    // after the fact. `noun` names the thing in the refusal, since a node and an entity read differently.
    internal static string? InvalidId(string id, string noun) =>
        !string.IsNullOrEmpty(id) && id.All(IsIdChar)
            ? null
            : $"A{("aeiou".Contains(noun[0]) ? "n" : "")} {noun} name must be one word of letters, digits or underscores.";

    internal static bool IsIdChar(char character) => char.IsLetterOrDigit(character) || character == '_';

    internal static string[] Lines(string source) => source.ReplaceLineEndings("\n").Split('\n');

    // Normalized like every other path here (Lines/string.Join), so an appended line never leaves the source
    // half CRLF and half LF.
    internal static string Append(string source, string line, string defaultHeader) =>
        string.IsNullOrWhiteSpace(source)
            ? $"{defaultHeader}\n{line}"
            : $"{string.Join("\n", Lines(source)).TrimEnd()}\n{line}";

    internal static string Indented(string text) => Indent + text;

    // Labels are written quoted, so the only characters that could break out of one are the quote itself and
    // anything that would end the line.
    internal static string Clean(string label) =>
        new(label.Select(character => character switch
        {
            '"' => '\'',
            _ => char.IsControl(character) ? ' ' : character,
        }).ToArray());

    // The lines past any YAML front matter and comments — the first of them is the header keyword.
    internal static IEnumerable<string> Body(string[] lines)
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

    private static string? Header(string source) =>
        Body(Lines(source)).FirstOrDefault()?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];

    // Every diagram type writes its objects with a different grammar; guessing at one corrupts the source instead
    // of failing, so a call that does not match this source's dialect is refused, naming the calls that do.
    private static DiagramEdit? Wrong(string source, DiagramEditDialect needed)
    {
        var actual = DialectOf(source);
        return actual == needed ? null : DiagramEdit.Refuse(actual switch
        {
            DiagramEditDialect.Flowchart => "This is a flowchart — its objects are edited with add_node, rename_node, remove_node, connect_nodes and disconnect_nodes.",
            DiagramEditDialect.Er => "This is an erDiagram — its objects are edited with add_entity, rename_entity, remove_entity, set_attribute, remove_attribute, relate_entities and unrelate_entities.",
            _ => "Editing one object at a time works on flowchart, graph and erDiagram sources — use edit_diagram for this one.",
        });
    }
}
