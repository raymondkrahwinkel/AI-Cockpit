using System.Text.RegularExpressions;

namespace Cockpit.Plugin.PromptLibrary;

// Handles the `{{variable}}` placeholders in a template body (#2): extracting the distinct names so the
// dialog can offer one field per variable, and substituting the filled-in values back into the body. A name
// is any run of characters between `{{` and `}}`, trimmed; matching is case-sensitive so
// `{{Target}}` and `{{target}}` are distinct fields. An unfilled placeholder is left as-is on
// substitution rather than blanked, so a partially-filled prompt still shows what is missing.
internal static partial class PromptVariables
{
    [GeneratedRegex(@"\{\{\s*([^{}]+?)\s*\}\}")]
    private static partial Regex PlaceholderRegex();

    // The distinct variable names in `body`, in first-seen order.
    public static IReadOnlyList<string> Extract(string? body)
    {
        var names = new List<string>();
        var seen = new HashSet<string>();
        foreach (Match match in PlaceholderRegex().Matches(body ?? string.Empty))
        {
            var name = match.Groups[1].Value.Trim();
            if (name.Length > 0 && seen.Add(name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    // Replaces each `{{name}}` with `values`[name]; leaves the placeholder untouched when no value is provided.
    public static string Substitute(string? body, IReadOnlyDictionary<string, string> values) =>
        PlaceholderRegex().Replace(body ?? string.Empty, match =>
        {
            var name = match.Groups[1].Value.Trim();
            return values.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value) ? value : match.Value;
        });
}
