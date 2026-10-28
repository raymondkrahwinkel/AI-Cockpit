// AC-1896 spike: ProseMirror JSON <-> the `default` Y.XmlFragment with YDotNet 0.6.0 alone, in the layout y-prosemirror uses: an element per
// node (its name the node type), node attributes as element attributes, the text of a node as one XmlText whose delta carries the marks
// as attributes ({ markName: markAttrs }). What 0.6.0 cannot say is recorded, not hidden: element attributes are strings only, and an
// Input has no null.
using System.Text.Json;
using System.Text.Json.Nodes;
using YDotNet.Document.Cells;
using JsonArray = System.Text.Json.Nodes.JsonArray;
using JsonObject = System.Text.Json.Nodes.JsonObject;
using YDotNet.Document.Transactions;
using YDotNet.Document.Types.XmlElements;
using YDotNet.Document.Types.XmlFragments;
using YDotNet.Document.Types.XmlTexts;

internal static class YdnFragment
{
    public static void Write(Transaction tx, XmlFragment fragment, JsonNode doc)
    {
        uint at = 0;
        foreach (var block in doc["content"]?.AsArray() ?? [])
        {
            WriteElement(tx, fragment.InsertElement(tx, at++, block!["type"]!.GetValue<string>()), block);
        }
    }

    private static void WriteElement(Transaction tx, XmlElement element, JsonNode node)
    {
        foreach (var (key, value) in node["attrs"]?.AsObject() ?? [])
        {
            if (value is not null)
            {
                // ponytail: 0.6.0 writes every attribute as a string (level "1", checked "false"); the probe measures what that costs.
                element.InsertAttribute(tx, key, value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : value.ToJsonString());
            }
        }
        uint at = 0;
        XmlText? run = null;
        uint runLength = 0;
        foreach (var child in node["content"]?.AsArray() ?? [])
        {
            if (child!["type"]!.GetValue<string>() == "text")
            {
                run ??= element.InsertText(tx, at++);
                var text = child["text"]!.GetValue<string>();
                using var marks = Marks(child["marks"]?.AsArray());
                run.Insert(tx, runLength, text, marks);
                runLength += (uint)text.EnumerateRunes().Sum(r => r.Utf16SequenceLength);
                continue;
            }
            run = null;
            runLength = 0;
            WriteElement(tx, element.InsertElement(tx, at++, child["type"]!.GetValue<string>()), child);
        }
    }

    private static Input Marks(JsonArray? marks)
    {
        // An insert without attributes takes the formatting of the text to its left (Y.Text semantics), so plain text says "no marks".
        if (marks is null || marks.Count == 0)
        {
            return Input.Object(new Dictionary<string, Input>());
        }
        return Input.Object(marks.ToDictionary(m => m!["type"]!.GetValue<string>(), m => ToInput(m!["attrs"] ?? new JsonObject())));
    }

    private static Input ToInput(JsonNode? value) => value switch
    {
        JsonObject o => Input.Object(o.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => ToInput(kv.Value))),
        JsonArray a => Input.Array(a.Select(ToInput).ToArray()),
        JsonValue v when v.GetValueKind() == JsonValueKind.String => Input.String(v.GetValue<string>()),
        JsonValue v when v.GetValueKind() is JsonValueKind.True or JsonValueKind.False => Input.Boolean(v.GetValue<bool>()),
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => v.TryGetValue<long>(out var l) ? Input.Long(l) : Input.Double(v.GetValue<double>()),
        // ponytail: Input has no null in 0.6.0; a null inside mark attrs is left out and the schema's default fills it on the JS side.
        _ => throw new NotSupportedException("null has no Input in YDotNet 0.6.0"),
    };

    /// <summary>The fragment as ProseMirror JSON, in the layout of *.pm.json (attrs as yrs gives them back: strings).</summary>
    public static JsonObject Read(Transaction tx, XmlFragment fragment)
    {
        var content = new JsonArray();
        for (uint i = 0; i < fragment.ChildLength(tx); i++)
        {
            content.Add(ReadElement(tx, fragment.Get(tx, i)!.XmlElement!));
        }
        return new JsonObject { ["type"] = "doc", ["content"] = content };
    }

    private static JsonObject ReadElement(Transaction tx, XmlElement element)
    {
        var node = new JsonObject { ["type"] = element.Tag(tx) };
        var attrs = new JsonObject();
        foreach (var (name, value) in element.Iterate(tx))
        {
            attrs[name] = value;
        }
        if (attrs.Count > 0)
        {
            node["attrs"] = attrs;
        }
        var content = new JsonArray();
        for (uint i = 0; i < element.ChildLength(tx); i++)
        {
            var child = element.Get(tx, i)!;
            if (child.Tag == OutputTag.XmlText)
            {
                foreach (var chunk in child.XmlText!.Chunks(tx))
                {
                    var text = new JsonObject { ["type"] = "text" };
                    if (chunk.Attributes.Count > 0)
                    {
                        text["marks"] = new JsonArray(chunk.Attributes.Select(kv => (JsonNode)new JsonObject { ["type"] = kv.Key, ["attrs"] = FromOutput(kv.Value) }).ToArray());
                    }
                    text["text"] = chunk.Data.String;
                    content.Add(text);
                }
            }
            else
            {
                content.Add(ReadElement(tx, child.XmlElement!));
            }
        }
        if (content.Count > 0)
        {
            node["content"] = content;
        }
        return node;
    }

    private static JsonNode? FromOutput(Output o) => o.Tag switch
    {
        OutputTag.String => o.String,
        OutputTag.Long => o.Long,
        OutputTag.Double => o.Double,
        OutputTag.Boolean => o.Boolean,
        OutputTag.Null or OutputTag.Undefined => null,
        OutputTag.JsonObject => new JsonObject(o.JsonObject!.Select(kv => KeyValuePair.Create(kv.Key, FromOutput(kv.Value)))),
        OutputTag.JsonArray => new JsonArray(o.JsonArray!.Select(FromOutput).ToArray()),
        _ => $"<{o.Tag}>",
    };
}
