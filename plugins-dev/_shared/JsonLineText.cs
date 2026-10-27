using System.Text;

namespace Cockpit.Plugins.Shared;

internal static class JsonLineText
{
    public static string ReplaceLoneSurrogateEscapes(string line)
    {
        List<int>? replacements = null;

        for (var index = 0; index < line.Length - 5; index++)
        {
            if (line[index] != '\\')
            {
                continue;
            }

            var slashEnd = index;
            while (slashEnd < line.Length && line[slashEnd] == '\\')
            {
                slashEnd++;
            }

            if ((slashEnd - index) % 2 == 0 || slashEnd >= line.Length - 4 || line[slashEnd] != 'u'
                || !_TryHex(line, slashEnd + 1, out var value))
            {
                index = slashEnd - 1;
                continue;
            }

            if (value is >= 0xD800 and <= 0xDBFF)
            {
                var next = slashEnd + 5;
                if (next >= line.Length - 5 || line[next] != '\\' || line[next + 1] != 'u'
                    || !_TryHex(line, next + 2, out var low) || low is < 0xDC00 or > 0xDFFF)
                {
                    (replacements ??= []).Add(slashEnd + 1);
                }
            }
            else if (value is >= 0xDC00 and <= 0xDFFF)
            {
                var previous = index - 6;
                if (previous < 0 || line[previous] != '\\' || line[previous + 1] != 'u'
                    || !_TryHex(line, previous + 2, out var high) || high is < 0xD800 or > 0xDBFF)
                {
                    (replacements ??= []).Add(slashEnd + 1);
                }
            }

            index = slashEnd + 4;
        }

        if (replacements is null)
        {
            return line;
        }

        var result = new StringBuilder(line);
        foreach (var replacement in replacements)
        {
            result[replacement] = 'F';
            result[replacement + 1] = 'F';
            result[replacement + 2] = 'F';
            result[replacement + 3] = 'D';
        }

        return result.ToString();
    }

    private static bool _TryHex(string text, int start, out int value)
    {
        value = 0;
        for (var index = start; index < start + 4; index++)
        {
            var digit = text[index] switch
            {
                >= '0' and <= '9' => text[index] - '0',
                >= 'a' and <= 'f' => text[index] - 'a' + 10,
                >= 'A' and <= 'F' => text[index] - 'A' + 10,
                _ => -1,
            };
            if (digit < 0)
            {
                return false;
            }

            value = value * 16 + digit;
        }

        return true;
    }
}
