using System.Text.RegularExpressions;

namespace Cockpit.Core.Markdown;

// The shape check for "could this code-span be a file path", nothing more. `Theme.axaml` and
// `System.Text.Json` are the same shape (a dotted identifier with a short tail) and this cannot tell
// them apart — only a disk probe can (`FilePathResolver`, `Cockpit.App`). What this rejects is what
// never had a chance either way: no separator, no short extension, or a space with nothing to anchor it.
public static partial class FilePathCandidate
{
    public static bool TryParse(string codeSpanText, out string path, out int? line)
    {
        path = string.Empty;
        line = null;

        var trimmed = codeSpanText.Trim();
        if (trimmed.Length == 0 || trimmed.Contains("://", StringComparison.Ordinal) ||
            trimmed.Contains('\n') || trimmed.Contains('\r'))
        {
            return false;
        }

        // `Foo.cs:594` or `Foo.cs:594:12` — agents write both; the column rides along only to be dropped.
        var lineSuffix = TrailingLineSuffixRegex().Match(trimmed);
        var candidate = lineSuffix.Success ? trimmed[..lineSuffix.Index] : trimmed;

        var hasSeparator = candidate.Contains('/') || candidate.Contains('\\');

        // A space is fine inside an actual path (`C:\Program Files\x.txt`) but is the giveaway for prose
        // or a shell flag (`git stash -u`) once there is no separator to anchor it.
        if (!hasSeparator && candidate.AsSpan().IndexOfAny(' ', '\t') >= 0)
        {
            return false;
        }

        var isAbsolute = candidate.StartsWith('/') || candidate.StartsWith(@"\\") ||
                          DriveLetterRegex().IsMatch(candidate);

        if (!isAbsolute && !hasSeparator && !ShortExtensionRegex().IsMatch(candidate))
        {
            return false;
        }

        path = candidate;
        line = lineSuffix.Success ? int.Parse(lineSuffix.Groups["line"].Value) : null;
        return true;
    }

    [GeneratedRegex(@":(?<line>\d+)(?::\d+)?$")]
    private static partial Regex TrailingLineSuffixRegex();

    [GeneratedRegex(@"^[A-Za-z]:[\\/]")]
    private static partial Regex DriveLetterRegex();

    [GeneratedRegex(@"\.[A-Za-z0-9]{1,5}$")]
    private static partial Regex ShortExtensionRegex();
}
