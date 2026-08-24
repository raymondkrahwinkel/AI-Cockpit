using System.Globalization;
using System.Text.Json.Nodes;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Cockpit.Plugin.Kubernetes.Helm;

// One resource out of a rendered Helm manifest (AC-1061 fase 2): its identity, the literal YAML helm rendered for
// it, and that YAML as JSON for the apply. The split is textual so the approval diff shows helm's own lines rather
// than a re-serialized copy that would differ from both revisions.
internal sealed record ManifestDocument(string ApiVersion, string Kind, string Name, string? Namespace, string Text)
{
    // Identity across two revisions. The rendered namespace is part of it: the same name in another namespace is
    // another resource, and a document that leaves it out inherits the release namespace on both sides alike.
    public string Key => $"{ApiVersion}|{Kind}|{Namespace}|{Name}";

    public string Display => Namespace is null ? $"{ApiVersion} {Kind} {Name}" : $"{ApiVersion} {Kind} {Namespace}/{Name}";

    // Splits a rendered manifest into its resources. Documents that carry no apiVersion/kind/name (helm emits empty
    // ones for templates that rendered to nothing) are dropped, and an unparseable document is reported through
    // `errors` rather than taken down the whole rollback with an exception.
    public static IReadOnlyList<ManifestDocument> SplitAll(string? manifest, out IReadOnlyList<string> errors)
    {
        var documents = new List<ManifestDocument>();
        var failures = new List<string>();
        errors = failures;
        if (string.IsNullOrWhiteSpace(manifest))
        {
            return documents;
        }

        foreach (var chunk in _SplitOnDocumentMarkers(manifest))
        {
            if (string.IsNullOrWhiteSpace(chunk))
            {
                continue;
            }

            YamlMappingNode? root;
            try
            {
                var stream = new YamlStream();
                stream.Load(new StringReader(chunk));
                root = stream.Documents.Count == 0 ? null : stream.Documents[0].RootNode as YamlMappingNode;
            }
            catch (YamlException exception)
            {
                failures.Add($"A manifest document could not be parsed as YAML ({exception.Message}).");
                continue;
            }

            if (root is null)
            {
                continue;
            }

            var apiVersion = _Scalar(root, "apiVersion");
            var kind = _Scalar(root, "kind");
            var metadata = _Child(root, "metadata") as YamlMappingNode;
            var name = metadata is null ? null : _Scalar(metadata, "name");
            if (apiVersion is null || kind is null || name is null)
            {
                failures.Add("A manifest document is missing apiVersion, kind or metadata.name.");
                continue;
            }

            documents.Add(new ManifestDocument(apiVersion, kind, name, metadata is null ? null : _Scalar(metadata, "namespace"), chunk.TrimEnd()));
        }

        return documents;
    }

    // The document as JSON, for the merge patch or the create. Null when the text no longer parses — the caller has
    // already split it once, so this only fires on a document that is not a mapping at all.
    public JsonObject? ToJson()
    {
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(Text));
            return stream.Documents.Count == 0 ? null : _ToJson(stream.Documents[0].RootNode) as JsonObject;
        }
        catch (YamlException)
        {
            return null;
        }
    }

    // A line that starts a YAML document ends the previous one. Inside a Kubernetes manifest nothing else can sit at
    // column 0 and read as "---", so this needs no YAML parser and keeps every original line intact.
    private static IEnumerable<string> _SplitOnDocumentMarkers(string manifest)
    {
        var current = new List<string>();
        foreach (var line in manifest.Replace("\r\n", "\n").Split('\n'))
        {
            if (line == "---" || line.StartsWith("--- ", StringComparison.Ordinal))
            {
                yield return string.Join('\n', current);
                current.Clear();
                continue;
            }

            current.Add(line);
        }

        yield return string.Join('\n', current);
    }

    private static YamlNode? _Child(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;

    private static string? _Scalar(YamlMappingNode node, string key) =>
        _Child(node, key) is YamlScalarNode { Value: { } text } ? text : null;

    private static JsonNode? _ToJson(YamlNode node) => node switch
    {
        YamlMappingNode map => _MapToJson(map),
        YamlSequenceNode sequence => new JsonArray(sequence.Children.Select(_ToJson).ToArray()),
        YamlScalarNode scalar => _ScalarToJson(scalar),
        _ => null,
    };

    private static JsonObject _MapToJson(YamlMappingNode map)
    {
        var json = new JsonObject();
        foreach (var (key, value) in map.Children)
        {
            if (key is YamlScalarNode { Value: { } name })
            {
                json[name] = _ToJson(value);
            }
        }

        return json;
    }

    // Only an unquoted scalar carries a type in YAML: `replicas: 3` is a number and `replicas: "3"` is a string, and
    // the apiserver rejects the wrong one. Quoted, literal and folded scalars stay strings whatever they spell.
    private static JsonNode? _ScalarToJson(YamlScalarNode scalar)
    {
        var text = scalar.Value;
        if (text is null)
        {
            return null;
        }

        if (scalar.Style != ScalarStyle.Plain && scalar.Style != ScalarStyle.Any)
        {
            return JsonValue.Create(text);
        }

        if (text.Length == 0 || text is "~" or "null" or "Null" or "NULL")
        {
            return null;
        }

        if (text is "true" or "True" or "TRUE")
        {
            return JsonValue.Create(true);
        }

        if (text is "false" or "False" or "FALSE")
        {
            return JsonValue.Create(false);
        }

        // A leading zero means YAML would read it as octal and helm would have quoted it if it meant a number, so
        // "010" stays the string it looks like rather than silently becoming 8 or 10.
        var digits = text.StartsWith('-') || text.StartsWith('+') ? text[1..] : text;
        if (digits.Length > 1 && digits[0] == '0' && digits[1] != '.')
        {
            return JsonValue.Create(text);
        }

        if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer))
        {
            return JsonValue.Create(integer);
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? JsonValue.Create(number)
            : JsonValue.Create(text);
    }
}
