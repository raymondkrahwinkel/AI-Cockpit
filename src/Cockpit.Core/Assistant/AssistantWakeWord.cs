using System.Globalization;
using System.Text.RegularExpressions;

namespace Cockpit.Core.Assistant;

public static partial class AssistantWakeWord
{
    public static bool IsConfigured(string? wakeWord) =>
        wakeWord?.Split(',', StringSplitOptions.TrimEntries) is { } variants
        && variants.All(word => word.Length is > 0 and <= 64 && ConfiguredWord().IsMatch(word));

    public static string? Match(string text, string wakeWord)
    {
        if (!IsConfigured(wakeWord))
        {
            return null;
        }

        string[] variants = wakeWord.Split(',', StringSplitOptions.TrimEntries);
        for (Match token = Word().Match(text); token.Success; token = token.NextMatch())
        {
            if (variants.Any(word => CultureInfo.InvariantCulture.CompareInfo.Compare(
                token.Value, word, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) == 0))
            {
                return text[(token.Index + token.Length)..].TrimStart(' ', '\t', '\r', '\n', ',', ':', ';', '!', '?', '.').TrimEnd();
            }
        }

        return null;
    }

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{M}\p{N}]*", RegexOptions.CultureInvariant)]
    private static partial Regex Word();

    [GeneratedRegex(@"\A[\p{L}\p{N}][\p{L}\p{M}\p{N}]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex ConfiguredWord();
}
