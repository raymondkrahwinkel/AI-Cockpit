using System.Text;
using System.Text.RegularExpressions;

namespace Cockpit.Plugin.Discord;

internal static partial class DiscordMessageText
{
    private const int Limit = 2000;

    public static IReadOnlyList<string> Split(string text)
    {
        var parts = new List<string>();
        var prose = new StringBuilder();
        var lines = text.Split('\n');
        string? fence = null;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                var marker = new string(trimmed.TakeWhile(character => character == trimmed[0]).ToArray());
                if (fence is null)
                {
                    fence = marker;
                }
                else if (trimmed.StartsWith(fence, StringComparison.Ordinal))
                {
                    fence = null;
                }
            }

            if (fence is null && !line.StartsWith("    ", StringComparison.Ordinal) && !line.StartsWith('\t')
                && index + 1 < lines.Length && line.Contains('|') && Separator().IsMatch(lines[index + 1]))
            {
                parts.AddRange(DiscordSendDmStep.Split(prose.ToString(), Limit));
                prose.Clear();
                var rows = new List<string[]> { Cells(line) };
                index += 2;
                while (index < lines.Length && lines[index].Contains('|') && !string.IsNullOrWhiteSpace(lines[index]))
                {
                    rows.Add(Cells(lines[index]));
                    index++;
                }

                index--;
                parts.AddRange(Table(rows));
                continue;
            }

            prose.Append(line);
            if (index < lines.Length - 1)
            {
                prose.Append('\n');
            }
        }

        parts.AddRange(DiscordSendDmStep.Split(prose.ToString(), Limit));
        return parts.Count == 0 ? ["\u200b"] : parts;
    }

    private static IReadOnlyList<string> Table(List<string[]> rows)
    {
        var columns = rows.Max(row => row.Length);
        var widths = Enumerable.Range(0, columns).Select(column => rows.Max(row => column < row.Length ? row[column].Length : 0)).ToArray();
        var parts = new List<string>();
        var block = new StringBuilder("```\n");
        foreach (var row in rows)
        {
            var rendered = string.Join("  ", Enumerable.Range(0, columns).Select(column => (column < row.Length ? row[column] : string.Empty).PadRight(widths[column]))).TrimEnd();
            if (rendered.Length > Limit - 8)
            {
                throw new ArgumentOutOfRangeException(nameof(rows), "A table row exceeds Discord's 2000-character message limit.");
            }

            if (block.Length + rendered.Length + 4 > Limit)
            {
                parts.Add(block.Append("```").ToString());
                block.Clear().Append("```\n");
            }

            block.Append(rendered).Append('\n');
        }

        parts.Add(block.Append("```").ToString());
        return parts;
    }

    private static string[] Cells(string line) =>
        Regex.Split(line.Trim().Trim('|'), @"(?<!\\)\|").Select(cell => cell.Trim().Replace("\\|", "|", StringComparison.Ordinal).Replace("```", "`\u200b``", StringComparison.Ordinal)).ToArray();

    [GeneratedRegex(@"^\s*\|?\s*:?-{3,}:?\s*(?:\|\s*:?-{3,}:?\s*)*\|?\s*$")]
    private static partial Regex Separator();
}
