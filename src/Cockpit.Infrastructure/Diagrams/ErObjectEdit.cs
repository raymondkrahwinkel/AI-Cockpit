using System.Text.RegularExpressions;
using Cockpit.Core.Abstractions.Diagrams;

namespace Cockpit.Infrastructure.Diagrams;

// The per-object grammar for erDiagram sources (AC-899), reached through DiagramObjectEdit once the header keyword
// says this is the dialect. An entity is a block over several lines rather than one line, so the surgery here finds
// and rewrites blocks; a relationship is still a single line, like a flowchart connection.
internal static partial class ErObjectEdit
{
    private const string DefaultHeader = "erDiagram";
    private const string IdentifyingLink = "--";

    public static DiagramEdit AddEntity(string source, string entity)
    {
        if (DiagramObjectEdit.InvalidId(entity, "entity") is { } refusal)
        {
            return DiagramEdit.Refuse(refusal);
        }

        var lines = DiagramObjectEdit.Lines(source);
        if (Block(lines, entity) is not null)
        {
            return DiagramEdit.Refuse($"Entity \"{entity}\" is already in this diagram — set_attribute adds to it.");
        }

        // A bare entity name on its own line draws nothing (measured on Mermaider 0.12.2), so a new entity is
        // written as an empty block, which does draw.
        var related = Relations(lines).Any(relation => Touches(relation, entity));
        var block = $"{DiagramObjectEdit.Indented(entity)} {{\n{DiagramObjectEdit.Indented("}")}";
        return DiagramEdit.Change(
            DiagramObjectEdit.Append(source, block, DefaultHeader),
            related ? $"gave entity {entity} a block of its own" : $"added entity {entity}");
    }

    // An ER entity's name *is* its identity: every relationship is written in terms of it, so unlike a flowchart
    // rename this one does rewrite those lines — leaving them behind would draw a second, empty entity.
    public static DiagramEdit RenameEntity(string source, string entity, string renamedTo)
    {
        if ((DiagramObjectEdit.InvalidId(entity, "entity") ?? DiagramObjectEdit.InvalidId(renamedTo, "entity")) is { } refusal)
        {
            return DiagramEdit.Refuse(refusal);
        }

        var lines = DiagramObjectEdit.Lines(source);
        if (!Exists(lines, entity))
        {
            return DiagramEdit.Refuse(NoSuchEntity(entity));
        }

        if (entity != renamedTo && Exists(lines, renamedTo))
        {
            return DiagramEdit.Refuse($"Entity \"{renamedTo}\" is already in this diagram, so renaming \"{entity}\" onto it would merge the two.");
        }

        if (Block(lines, entity) is { } block)
        {
            lines[block.Open] = $"{Indentation(lines[block.Open])}{renamedTo} {{";
        }

        foreach (var relation in Relations(lines).Where(relation => Touches(relation, entity)).ToList())
        {
            lines[relation.Line] = Write(
                Indentation(lines[relation.Line]),
                relation with
                {
                    From = relation.From == entity ? renamedTo : relation.From,
                    To = relation.To == entity ? renamedTo : relation.To,
                });
        }

        return DiagramEdit.Change(string.Join("\n", lines), $"renamed entity {entity} to {renamedTo}");
    }

    // A relationship whose entity is gone would draw that entity again on the next render, so the entity's own
    // relationships go with it — nothing else does.
    public static DiagramEdit RemoveEntity(string source, string entity)
    {
        if (DiagramObjectEdit.InvalidId(entity, "entity") is { } refusal)
        {
            return DiagramEdit.Refuse(refusal);
        }

        var lines = DiagramObjectEdit.Lines(source);
        if (!Exists(lines, entity))
        {
            return DiagramEdit.Refuse(NoSuchEntity(entity));
        }

        var block = Block(lines, entity);
        var dropped = Relations(lines).Where(relation => Touches(relation, entity)).Select(relation => relation.Line).ToHashSet();
        var kept = lines.Where((_, i) => !dropped.Contains(i) && !(block is { } at && i >= at.Open && i <= at.Close));

        var summary = dropped.Count == 0
            ? $"removed entity {entity}"
            : $"removed entity {entity} and its {dropped.Count} relationship{(dropped.Count == 1 ? "" : "s")}";
        return DiagramEdit.Change(string.Join("\n", kept), summary);
    }

    // Adding and changing an attribute are the same surgery — the line is written as it should read, whether or not
    // one was already there — so one call covers both and the caller never has to know which it is doing.
    public static DiagramEdit SetAttribute(string source, string entity, string attribute, string type, string? key)
    {
        if ((DiagramObjectEdit.InvalidId(entity, "entity") ?? DiagramObjectEdit.InvalidId(attribute, "attribute")) is { } refusal)
        {
            return DiagramEdit.Refuse(refusal);
        }

        if (string.IsNullOrWhiteSpace(type) || type.Any(character => char.IsWhiteSpace(character) || character == '"'))
        {
            return DiagramEdit.Refuse("An attribute type must be one word without quotes, such as \"string\", \"int\" or \"varchar(50)\".");
        }

        var marker = Normalize(key);
        if (marker is null && !string.IsNullOrWhiteSpace(key))
        {
            return DiagramEdit.Refuse("An attribute key must be PK, FK or UK, or left out entirely.");
        }

        var lines = DiagramObjectEdit.Lines(source).ToList();
        if (!Exists(lines, entity))
        {
            return DiagramEdit.Refuse(NoSuchEntity(entity));
        }

        // An entity that only appears in relationships has no block to write into yet; giving it one here is what
        // the operator asked for by adding an attribute to it.
        var block = Block(lines, entity);
        if (block is null)
        {
            lines.Add($"{DiagramObjectEdit.Indented(entity)} {{");
            lines.Add(DiagramObjectEdit.Indented("}"));
            block = (lines.Count - 2, lines.Count - 1);
        }

        var (open, close) = block.Value;
        var existing = Rows(lines, open, close).FirstOrDefault(row => row.Attribute.Name == attribute);
        var line = Indentation(lines[open]) + "    " + Write(new DiagramErAttribute(type, attribute, marker), existing?.Comment);
        if (existing is not null)
        {
            lines[existing.Line] = line;
            return DiagramEdit.Change(string.Join("\n", lines), $"changed attribute {entity}.{attribute} to {type}");
        }

        lines.Insert(close, line);
        return DiagramEdit.Change(string.Join("\n", lines), $"added attribute {entity}.{attribute} ({type})");
    }

    public static DiagramEdit RemoveAttribute(string source, string entity, string attribute)
    {
        if ((DiagramObjectEdit.InvalidId(entity, "entity") ?? DiagramObjectEdit.InvalidId(attribute, "attribute")) is { } refusal)
        {
            return DiagramEdit.Refuse(refusal);
        }

        var lines = DiagramObjectEdit.Lines(source);
        if (Block(lines, entity) is not { } block)
        {
            return DiagramEdit.Refuse($"Entity \"{entity}\" has no attributes in this diagram.");
        }

        if (Rows(lines, block.Open, block.Close).FirstOrDefault(row => row.Attribute.Name == attribute) is not { } found)
        {
            return DiagramEdit.Refuse($"Entity \"{entity}\" has no attribute \"{attribute}\" — read_diagram shows what is there.");
        }

        return DiagramEdit.Change(
            string.Join("\n", lines.Where((_, i) => i != found.Line)),
            $"removed attribute {entity}.{attribute}");
    }

    // Laying a relationship and changing one are the same surgery, for the same reason as SetAttribute. An existing
    // relationship keeps its identifying/non-identifying line style: nobody asked for that to change.
    public static DiagramEdit Relate(string source, string from, string to, DiagramErCardinality? fromCardinality, DiagramErCardinality? toCardinality, string? label)
    {
        if ((DiagramObjectEdit.InvalidId(from, "entity") ?? DiagramObjectEdit.InvalidId(to, "entity")) is { } refusal)
        {
            return DiagramEdit.Refuse(refusal);
        }

        if (fromCardinality is not { } tail || toCardinality is not { } head)
        {
            return DiagramEdit.Refuse("A relationship needs a cardinality on both ends: one, zero-or-one, one-or-more or zero-or-more.");
        }

        if (string.IsNullOrWhiteSpace(label))
        {
            return DiagramEdit.Refuse("A relationship needs a label — that is the verb Mermaid draws on the line, and it is not optional here.");
        }

        var lines = DiagramObjectEdit.Lines(source).ToList();
        var text = DiagramObjectEdit.Clean(label.Trim());
        if (Relations(lines).FirstOrDefault(relation => relation.From == from && relation.To == to) is { } existing)
        {
            lines[existing.Line] = Write(Indentation(lines[existing.Line]), existing with { FromCardinality = tail, ToCardinality = head, Label = text });
            return DiagramEdit.Change(string.Join("\n", lines), $"changed relationship {from} -> {to} \"{text}\"");
        }

        var line = Write("", new Relation(0, from, to, tail, head, IdentifyingLink, text));
        return DiagramEdit.Change(
            DiagramObjectEdit.Append(source, DiagramObjectEdit.Indented(line), DefaultHeader),
            $"related {from} -> {to} \"{text}\"");
    }

    public static DiagramEdit Unrelate(string source, string from, string to)
    {
        if ((DiagramObjectEdit.InvalidId(from, "entity") ?? DiagramObjectEdit.InvalidId(to, "entity")) is { } refusal)
        {
            return DiagramEdit.Refuse(refusal);
        }

        var lines = DiagramObjectEdit.Lines(source);
        if (Relations(lines).FirstOrDefault(relation => relation.From == from && relation.To == to) is not { } existing)
        {
            return DiagramEdit.Refuse($"There is no {from} -> {to} relationship in this diagram.");
        }

        return DiagramEdit.Change(
            string.Join("\n", lines.Where((_, i) => i != existing.Line)),
            $"unrelated {from} -> {to}");
    }

    public static IReadOnlyList<DiagramErAttribute> Attributes(string source, string entity)
    {
        var lines = DiagramObjectEdit.Lines(source);
        return Block(lines, entity) is not { } block
            ? []
            : Rows(lines, block.Open, block.Close).Select(row => row.Attribute).ToList();
    }

    // The edit that undoes one journaled ER handling against the source as it stands now (AC-853): what this edit
    // added is taken back structurally, what it replaced is written back from the line it removed.
    public static DiagramEdit Invert(string source, DiagramHandEditKind kind, string objectKey, IReadOnlyList<string> removedLines)
    {
        if (kind == DiagramHandEditKind.AddEntity)
        {
            return RemoveEntity(source, objectKey);
        }

        if (kind == DiagramHandEditKind.RenameEntity)
        {
            return objectKey.Split('>') is [var was, var now]
                ? RenameEntity(source, now, was)
                : DiagramEdit.Refuse(Unrecognized);
        }

        if (kind is DiagramHandEditKind.SetAttribute or DiagramHandEditKind.RemoveAttribute)
        {
            return objectKey.Split('.') is not [var owner, var attribute]
                ? DiagramEdit.Refuse(Unrecognized)
                : removedLines.Select(ReadAttribute).FirstOrDefault(row => row is not null) is { } previous
                    ? SetAttribute(source, owner, previous.Attribute.Name, previous.Attribute.Type, previous.Attribute.Key)
                    : RemoveAttribute(source, owner, attribute);
        }

        return objectKey.Split("->") is not [var from, var to]
            ? DiagramEdit.Refuse(Unrecognized)
            : removedLines.Select(line => Read(line, 0)).FirstOrDefault(relation => relation is not null) is { } replaced
                ? Relate(source, from, to, replaced.FromCardinality, replaced.ToCardinality, replaced.Label)
                : Unrelate(source, from, to);
    }

    // ---- reading the source ----

    private sealed record Relation(int Line, string From, string To, DiagramErCardinality FromCardinality, DiagramErCardinality ToCardinality, string Link, string Label);

    private sealed record AttributeRow(int Line, DiagramErAttribute Attribute, string? Comment);

    // The line indexes of ENTITY's block header and its closing brace. ER blocks do not nest, so the first '}' at
    // or past the header closes it; a header without one is not a usable block at all.
    private static (int Open, int Close)? Block(IReadOnlyList<string> lines, string entity)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (BlockHeader().Match(lines[i].Trim()) is not { Success: true } header || header.Groups["name"].Value != entity)
            {
                continue;
            }

            for (var close = i + 1; close < lines.Count; close++)
            {
                if (lines[close].Trim() == "}")
                {
                    return (i, close);
                }
            }

            return null;
        }

        return null;
    }

    private static IEnumerable<Relation> Relations(IReadOnlyList<string> lines) =>
        lines.Select(Read).OfType<Relation>();

    private static IEnumerable<AttributeRow> Rows(IReadOnlyList<string> lines, int open, int close) =>
        Enumerable.Range(open + 1, Math.Max(0, close - open - 1))
            .Select(i => ReadAttribute(lines[i]) is { } row ? new AttributeRow(i, row.Attribute, row.Comment) : null)
            .OfType<AttributeRow>();

    private static (DiagramErAttribute Attribute, string? Comment)? ReadAttribute(string line) =>
        AttributeLine().Match(line.Trim()) is { Success: true } match
            ? (new DiagramErAttribute(match.Groups["type"].Value, match.Groups["name"].Value, Normalize(match.Groups["key"].Value)),
               match.Groups["comment"].Success ? match.Groups["comment"].Value : null)
            : null;

    private static Relation? Read(string line, int index) =>
        Relationship().Match(line.Trim()) is { Success: true } match
            ? new Relation(
                index,
                match.Groups["from"].Value,
                match.Groups["to"].Value,
                LeftCardinality(match.Groups["left"].Value),
                RightCardinality(match.Groups["right"].Value),
                match.Groups["link"].Value,
                match.Groups["label"].Value.Trim().Trim('"'))
            : null;

    private static bool Exists(IReadOnlyList<string> lines, string entity) =>
        Block(lines, entity) is not null || Relations(lines).Any(relation => Touches(relation, entity));

    private static bool Touches(Relation relation, string entity) => relation.From == entity || relation.To == entity;

    // ---- writing the source ----

    private static string Write(string indent, Relation relation) =>
        $"{indent}{relation.From} {Left(relation.FromCardinality)}{relation.Link}{Right(relation.ToCardinality)} {relation.To} : \"{relation.Label}\"";

    private static string Write(DiagramErAttribute attribute, string? comment) =>
        string.Join(" ", new[] { attribute.Type, attribute.Name, attribute.Key, comment }.Where(part => !string.IsNullOrEmpty(part)));

    private static string Indentation(string line) => line[..(line.Length - line.TrimStart().Length)];

    private static string? Normalize(string? key) =>
        key?.Trim().ToUpperInvariant() is { Length: > 0 } text && text is "PK" or "FK" or "UK" ? text : null;

    private static string Left(DiagramErCardinality cardinality) => cardinality switch
    {
        DiagramErCardinality.One => "||",
        DiagramErCardinality.ZeroOrOne => "|o",
        DiagramErCardinality.OneOrMore => "}|",
        _ => "}o",
    };

    private static string Right(DiagramErCardinality cardinality) => cardinality switch
    {
        DiagramErCardinality.One => "||",
        DiagramErCardinality.ZeroOrOne => "o|",
        DiagramErCardinality.OneOrMore => "|{",
        _ => "o{",
    };

    private static DiagramErCardinality LeftCardinality(string token) => token switch
    {
        "||" => DiagramErCardinality.One,
        "|o" => DiagramErCardinality.ZeroOrOne,
        "}|" => DiagramErCardinality.OneOrMore,
        _ => DiagramErCardinality.ZeroOrMore,
    };

    private static DiagramErCardinality RightCardinality(string token) => token switch
    {
        "||" => DiagramErCardinality.One,
        "o|" => DiagramErCardinality.ZeroOrOne,
        "|{" => DiagramErCardinality.OneOrMore,
        _ => DiagramErCardinality.ZeroOrMore,
    };

    private const string Unrecognized = "That handling can no longer be matched to an object in this diagram.";

    private static string NoSuchEntity(string entity) =>
        $"There is no entity \"{entity}\" in this diagram — read_diagram shows what is there.";

    [GeneratedRegex(@"^(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\{$")]
    private static partial Regex BlockHeader();

    [GeneratedRegex(@"^(?<from>[A-Za-z_][A-Za-z0-9_]*)\s+(?<left>\|\||\|o|\}\||\}o)(?<link>--|\.\.)(?<right>\|\||o\||\|\{|o\{)\s+(?<to>[A-Za-z_][A-Za-z0-9_]*)\s*:\s*(?<label>.*)$")]
    private static partial Regex Relationship();

    [GeneratedRegex(@"^(?<type>[^\s""]+)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)(?:\s+(?<key>PK|FK|UK))?(?:\s+(?<comment>""[^""]*""))?$")]
    private static partial Regex AttributeLine();
}
