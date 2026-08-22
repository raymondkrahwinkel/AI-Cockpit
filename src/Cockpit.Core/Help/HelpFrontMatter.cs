namespace Cockpit.Core.Help;

// AC-1033: the `---`-fenced `key: value` block each documentation file opens with. Deliberately not a YAML
// parser — a colon and a trimmed value carry every key this needs.
public sealed record HelpFrontMatter
{
    public string? Title { get; init; }

    public string? Summary { get; init; }

    public int Order { get; init; }

    public string? Icon { get; init; }

    // Only read for the core's own pages. A plugin's page always lands under `Plugins`, so this key is
    // ignored there rather than rejected: a plugin author who tries it gets the documented behaviour, not
    // a page that silently fails to appear.
    public string? Category { get; init; }

    public static HelpFrontMatter Parse(string text, out string body)
    {
        var normalised = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        body = normalised;

        if (!normalised.StartsWith("---\n", StringComparison.Ordinal))
        {
            return new HelpFrontMatter();
        }

        var end = normalised.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0)
        {
            return new HelpFrontMatter();
        }

        var block = normalised[4..(end + 1)];
        var after = normalised.IndexOf('\n', end + 1);
        body = after < 0 ? string.Empty : normalised[(after + 1)..].TrimStart('\n');

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in block.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            values[line[..colon].Trim()] = line[(colon + 1)..].Trim().Trim('"', '\'');
        }

        return new HelpFrontMatter
        {
            Title = _Value(values, "title"),
            Summary = _Value(values, "summary"),
            Icon = _Value(values, "icon"),
            Category = _Value(values, "category"),
            Order = int.TryParse(_Value(values, "order"), out var order) ? order : 0,
        };
    }

    private static string? _Value(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && value.Length > 0 ? value : null;
}
