using System.Text.RegularExpressions;

namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// The outcome of resolving a template body (AC-189): the filled-in <see cref="Text"/>, and the names of any
/// placeholders that could not be filled. Resolving never fails — an unfillable placeholder is replaced with the empty
/// string and its name collected here — so the surface can warn about the gaps rather than a run dying on a bad token.
/// </summary>
/// <param name="Text">The body with every <c>{{placeholder}}</c> replaced (missing ones by the empty string).</param>
/// <param name="MissingPlaceholders">The placeholder names that had no value, in first-seen order, de-duplicated.</param>
internal sealed record AutopilotTemplateResolution(string Text, IReadOnlyList<string> MissingPlaceholders);

/// <summary>
/// Fills the <c>{{placeholder}}</c> tokens in an <see cref="AutopilotTemplate.Body"/> (AC-189). It reads two sources
/// and merges them in one pass:
/// <list type="bullet">
/// <item><c>{{issue.id}}</c>, <c>{{issue.title}}</c>, <c>{{issue.description}}</c>, <c>{{issue.url}}</c> and
/// <c>{{issue.tracker}}</c> from a tracker intent's <c>Data</c> dictionary — the same payload a tracker sends on the
/// "plan" intent (see <see cref="AutopilotRun.FromIntent"/>), keyed there as <c>issue</c>, <c>title</c>,
/// <c>description</c>, <c>url</c> and <c>tracker</c>.</item>
/// <item><c>{{input.&lt;name&gt;}}</c> from an operator-supplied input dictionary.</item>
/// </list>
/// It never throws. An unknown token, or one whose value is absent, is replaced with the empty string and its name is
/// reported in <see cref="AutopilotTemplateResolution.MissingPlaceholders"/> so the caller can warn. It only rewrites
/// the body string it is handed — the C# brief texts elsewhere interpolate at compile time and never pass through here,
/// so the runtime <c>{{...}}</c> syntax cannot collide with them.
/// </summary>
internal static partial class AutopilotTemplateResolver
{
    // {{ token }} — the token is issue.<field> or input.<name>; surrounding whitespace is tolerated and trimmed. The
    // token itself excludes braces so an unclosed "{{" cannot swallow the rest of the body.
    [GeneratedRegex(@"\{\{\s*([^{}]+?)\s*\}\}")]
    private static partial Regex TokenPattern();

    // Maps each issue.* token to the key a tracker intent carries it under in its Data dictionary (AutopilotRun.FromIntent).
    private static readonly IReadOnlyDictionary<string, string> IssueDataKeys = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["issue.id"] = "issue",
        ["issue.title"] = "title",
        ["issue.description"] = "description",
        ["issue.url"] = "url",
        ["issue.tracker"] = "tracker",
    };

    /// <summary>
    /// Resolves <paramref name="body"/> against a tracker intent's <paramref name="intentData"/> and the operator's
    /// <paramref name="input"/>. Either source may be null (a CEO-first run has no intent data; a template may take no
    /// input). Missing values are filled with the empty string and reported, never thrown.
    /// </summary>
    public static AutopilotTemplateResolution Resolve(
        string body,
        IReadOnlyDictionary<string, string>? intentData = null,
        IReadOnlyDictionary<string, string>? input = null)
    {
        var missing = new List<string>();
        var text = TokenPattern().Replace(body ?? string.Empty, match =>
        {
            var token = match.Groups[1].Value;
            if (_TryResolve(token, intentData, input, out var value))
            {
                return value;
            }

            if (!missing.Contains(token, StringComparer.Ordinal))
            {
                missing.Add(token);
            }

            return string.Empty;
        });

        return new AutopilotTemplateResolution(text, missing);
    }

    // True with the token's value when it resolves; false (and an empty value) when the token is unknown or its value
    // is absent. A present-but-empty issue field (a blank description) still counts as resolved — the key was there.
    private static bool _TryResolve(
        string token,
        IReadOnlyDictionary<string, string>? intentData,
        IReadOnlyDictionary<string, string>? input,
        out string value)
    {
        if (IssueDataKeys.TryGetValue(token, out var dataKey))
        {
            if (intentData is not null && intentData.TryGetValue(dataKey, out var issueValue))
            {
                value = issueValue;
                return true;
            }

            value = string.Empty;
            return false;
        }

        if (token.StartsWith("input.", StringComparison.Ordinal))
        {
            var name = token["input.".Length..];
            if (name.Length > 0 && input is not null && input.TryGetValue(name, out var inputValue))
            {
                value = inputValue;
                return true;
            }

            value = string.Empty;
            return false;
        }

        value = string.Empty;
        return false;
    }
}
