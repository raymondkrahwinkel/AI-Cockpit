using System.Text.RegularExpressions;

namespace Cockpit.Plugin.PromptLibrary;

/// <summary>
/// Handles the <c>{{variable}}</c> placeholders in a template body (#2): extracting the distinct names so the
/// dialog can offer one field per variable, and substituting the filled-in values back into the body. A name
/// is any run of characters between <c>{{</c> and <c>}}</c>, trimmed; matching is case-sensitive so
/// <c>{{Target}}</c> and <c>{{target}}</c> are distinct fields. An unfilled placeholder is left as-is on
/// substitution rather than blanked, so a partially-filled prompt still shows what is missing.
/// </summary>
internal static partial class PromptVariables
{
    [GeneratedRegex(@"\{\{\s*([^{}]+?)\s*\}\}")]
    private static partial Regex PlaceholderRegex();

    /// <summary>The distinct variable names in <paramref name="body"/>, in first-seen order.</summary>
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

    /// <summary>Replaces each <c>{{name}}</c> with <paramref name="values"/>[name]; leaves the placeholder untouched when no value is provided.</summary>
    public static string Substitute(string? body, IReadOnlyDictionary<string, string> values) =>
        PlaceholderRegex().Replace(body ?? string.Empty, match =>
        {
            var name = match.Groups[1].Value.Trim();
            return values.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value) ? value : match.Value;
        });
}
