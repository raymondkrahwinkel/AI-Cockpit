using System.Text;
using Cockpit.Plugin.Diagram.Wireframe.Model;

namespace Cockpit.Plugin.Diagram.Wireframe;

// The whole wireframe language (AC-871): one component per line, `type "tekst" modifiers…`, nesting by
// indentation. Nothing here is executable and nothing carries coordinates — the renderer decides placement.
internal static class WireframeParser
{
    public static WireframeParseResult Parse(string source)
    {
        var errors = new List<WireframeParseError>();
        var open = new List<(int Indent, WireframeNode Node)>();
        WireframeNode? root = null;

        var lines = source.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var lineNumber = index + 1;
            var line = lines[index].TrimEnd('\r', ' ');
            if (line.Length == 0)
            {
                continue;
            }

            var content = line.TrimStart(' ');
            var indent = line.Length - content.Length;

            if (content[0] == '\t')
            {
                errors.Add(new WireframeParseError(lineNumber, "Spring in met spaties, niet met tabs."));
                continue;
            }

            var node = _ReadNode(content, lineNumber, errors);
            if (node is null)
            {
                continue;
            }

            // Close every level this line stepped out of. A dedent has to land exactly on a level that is still
            // open — otherwise the line reads as a child of something it is not lined up with.
            var dedented = false;
            while (open.Count > 0 && indent < open[^1].Indent)
            {
                open.RemoveAt(open.Count - 1);
                dedented = true;
            }

            if (dedented && (open.Count == 0 || indent != open[^1].Indent))
            {
                errors.Add(new WireframeParseError(lineNumber, "De inspringing hoort bij geen enkele regel hierboven."));
                continue;
            }

            if (open.Count > 0 && indent == open[^1].Indent)
            {
                open.RemoveAt(open.Count - 1);
            }

            var parent = open.Count > 0 ? open[^1].Node : null;
            if (parent is null)
            {
                if (root is not null)
                {
                    errors.Add(new WireframeParseError(lineNumber, "Een wireframe bevat één scherm; deze regel staat ernaast."));
                    continue;
                }

                if (indent > 0 || node.Kind != WireframeNodeKind.Screen)
                {
                    errors.Add(new WireframeParseError(lineNumber, "Een wireframe begint met een 'screen'-regel zonder inspringing."));
                    return new WireframeParseResult(null, errors);
                }

                root = node;
            }
            else if (parent.IsContainer)
            {
                parent.Children.Add(node);
            }
            else
            {
                var keyword = parent.Kind.ToString().ToLowerInvariant();
                errors.Add(new WireframeParseError(lineNumber, $"'{keyword}' kan geen onderliggende regels dragen."));
                continue;
            }

            open.Add((indent, node));
        }

        return new WireframeParseResult(root, errors);
    }

    private static WireframeNode? _ReadNode(string content, int lineNumber, List<WireframeParseError> errors)
    {
        var position = 0;
        var word = _ReadWord(content, ref position);
        if (word.Length == 0
            || (position < content.Length && content[position] != ' ')
            || !Enum.TryParse<WireframeNodeKind>(word, ignoreCase: true, out var kind))
        {
            errors.Add(new WireframeParseError(lineNumber, $"Onbekend component '{_TokenAt(content, 0)}'."));
            return null;
        }

        var node = new WireframeNode(kind, lineNumber);
        while (true)
        {
            while (position < content.Length && content[position] == ' ')
            {
                position++;
            }

            if (position >= content.Length)
            {
                return node;
            }

            if (content[position] == '"')
            {
                if (!_TryReadQuoted(content, ref position, out var text))
                {
                    errors.Add(new WireframeParseError(lineNumber, "Het aanhalingsteken is niet gesloten."));
                    return null;
                }

                if (node.Text is not null || node.Modifiers.Count > 0)
                {
                    errors.Add(new WireframeParseError(lineNumber, "De tekst hoort direct achter het component te staan."));
                    return null;
                }

                node.Text = text;
                continue;
            }

            var modifier = _ReadModifier(content, ref position, lineNumber, errors);
            if (modifier is null)
            {
                return null;
            }

            node.Modifiers.Add(modifier);
        }
    }

    private static WireframeModifier? _ReadModifier(string content, ref int position, int lineNumber, List<WireframeParseError> errors)
    {
        var start = position;
        var name = _ReadWord(content, ref position);
        var written = _TokenAt(content, start);

        if (name.Length == 0
            || (position < content.Length && content[position] is not (' ' or ':'))
            || !Enum.TryParse<WireframeModifierName>(name, ignoreCase: true, out var modifierName))
        {
            errors.Add(new WireframeParseError(lineNumber, $"Onbekende modifier '{written}'."));
            return null;
        }

        string? value = null;
        var isQuoted = false;
        if (position < content.Length && content[position] == ':')
        {
            position++;
            if (position < content.Length && content[position] == '"')
            {
                if (!_TryReadQuoted(content, ref position, out var quoted))
                {
                    errors.Add(new WireframeParseError(lineNumber, "Het aanhalingsteken is niet gesloten."));
                    return null;
                }

                value = quoted;
                isQuoted = true;
            }
            else
            {
                var valueStart = position;
                while (position < content.Length && content[position] != ' ')
                {
                    position++;
                }

                value = content[valueStart..position];
            }
        }

        return _Validate(modifierName, value, isQuoted, name, lineNumber, errors);
    }

    private static WireframeModifier? _Validate(
        WireframeModifierName name,
        string? value,
        bool isQuoted,
        string written,
        int lineNumber,
        List<WireframeParseError> errors)
    {
        var takesValue = name is WireframeModifierName.W
            or WireframeModifierName.H
            or WireframeModifierName.Align
            or WireframeModifierName.Value;

        if (takesValue && string.IsNullOrEmpty(value))
        {
            errors.Add(new WireframeParseError(lineNumber, $"'{written}' heeft een waarde nodig, zoals '{written}:2'."));
            return null;
        }

        if (!takesValue && value is not null)
        {
            errors.Add(new WireframeParseError(lineNumber, $"'{written}' neemt geen waarde."));
            return null;
        }

        switch (name)
        {
            case WireframeModifierName.W or WireframeModifierName.H
                when !int.TryParse(value, out var weight) || weight <= 0:
                errors.Add(new WireframeParseError(lineNumber, $"Het gewicht bij '{written}' moet een positief geheel getal zijn."));
                return null;
            case WireframeModifierName.Align
                when !Enum.TryParse<WireframeAlignment>(value, ignoreCase: true, out _):
                errors.Add(new WireframeParseError(lineNumber, $"Onbekende uitlijning '{value}' — kies left, center of right."));
                return null;
            default:
                return new WireframeModifier(name, value, isQuoted);
        }
    }

    private static string _ReadWord(string content, ref int position)
    {
        var start = position;
        while (position < content.Length && char.IsAsciiLetter(content[position]))
        {
            position++;
        }

        return content[start..position];
    }

    // A backslash escapes the next character, so a label can carry a quote of its own.
    private static bool _TryReadQuoted(string content, ref int position, out string value)
    {
        var builder = new StringBuilder();
        position++;

        while (position < content.Length)
        {
            var character = content[position++];
            if (character == '\\' && position < content.Length)
            {
                builder.Append(content[position++]);
                continue;
            }

            if (character == '"')
            {
                value = builder.ToString();
                return true;
            }

            builder.Append(character);
        }

        value = string.Empty;
        return false;
    }

    // What the operator actually typed at this spot, for an error message that quotes them rather than us.
    private static string _TokenAt(string content, int start)
    {
        var end = content.IndexOf(' ', start);
        return end < 0 ? content[start..] : content[start..end];
    }
}
