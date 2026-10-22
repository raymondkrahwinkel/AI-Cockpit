using System.Text.RegularExpressions;

namespace Cockpit.Core.Sessions;

// The assistant's per-project reply blocks (voorstel § 9.4): `<!-- project:depot -->` on its own line opens the block
// for that project. A marker rather than a tool call so the reply keeps streaming; the markdown parser hides it.
public static partial class ProjectMarkers
{
    public static IReadOnlyList<string> In(string text) =>
        string.IsNullOrEmpty(text) || !text.Contains("<!--", StringComparison.Ordinal)
            ? []
            : [.. MarkerRegex().Matches(text).Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal)];

    [GeneratedRegex(@"^\s*<!--\s*project:\s*([A-Za-z0-9._-]+)\s*-->\s*$", RegexOptions.Multiline)]
    private static partial Regex MarkerRegex();
}
