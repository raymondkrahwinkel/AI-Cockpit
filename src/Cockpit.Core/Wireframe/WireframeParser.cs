using System.Text;
using Cockpit.Core.Wireframe.Model;

namespace Cockpit.Core.Wireframe;

// The whole wireframe language (AC-871): one component per line, `type "tekst" modifiers…`, nesting by
// indentation. Nothing here is executable and nothing carries coordinates — the renderer decides placement.
public static class WireframeParser
{
    public static WireframeParseResult Parse(string source)
    {
        var errors = new List<WireframeParseError>();
        var open = new List<(int Indent, WireframeNode Node)>();
        var taken = new HashSet<string>(StringComparer.Ordinal);
        var screens = new List<WireframeNode>();
        WireframeViewport? viewport = null;

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
                errors.Add(new WireframeParseError(lineNumber, "Indent with spaces, not tabs."));
                continue;
            }

            // AC-915: recognised ahead of _ReadNode, and ahead of the hard-return below that stops the whole parse
            // before the first screen — a viewport line lives in exactly that spot, so reading it late would make
            // every document that declares one unreadable.
            if (_TryReadViewportKeyword(content, out var afterKeyword))
            {
                if (indent > 0)
                {
                    errors.Add(new WireframeParseError(lineNumber, "A 'viewport' line stands at the left margin, like a screen."));
                }
                else if (screens.Count > 0)
                {
                    errors.Add(new WireframeParseError(lineNumber, "The viewport belongs at the top, above the first screen."));
                }
                else if (viewport is not null)
                {
                    errors.Add(new WireframeParseError(lineNumber, "This wireframe already declares a viewport."));
                }
                else
                {
                    var name = content[afterKeyword..].TrimStart(' ');
                    if (!_ViewportNames.TryGetValue(name, out var parsedViewport))
                    {
                        errors.Add(new WireframeParseError(lineNumber, $"'{name}' is not a viewport — use desktop, tablet or mobile."));
                    }
                    else
                    {
                        viewport = parsedViewport;
                    }
                }

                continue;
            }

            var node = _ReadNode(content, lineNumber, errors);
            if (node is null)
            {
                continue;
            }

            // AC-906: two components answering to one id makes every call naming it a coin flip, which is the whole
            // thing an id exists to rule out. The line is refused, like any other one the format cannot read.
            if (node.Id is { } id && !taken.Add(id))
            {
                errors.Add(new WireframeParseError(lineNumber, $"The id '#{id}' is already in use in this wireframe."));
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
                errors.Add(new WireframeParseError(lineNumber, "The indentation does not match any line above."));
                continue;
            }

            if (open.Count > 0 && indent == open[^1].Indent)
            {
                open.RemoveAt(open.Count - 1);
            }

            var parent = open.Count > 0 ? open[^1].Node : null;
            if (parent is null)
            {
                // AC-901: every `screen` at the left margin is a screen of its own, so a document holds as many as
                // it needs. Anything else out here belongs under one of them; before the first screen there is
                // nothing to hang it on at all, which is the one case that stops the read.
                if (indent > 0 || node.Kind != WireframeNodeKind.Screen)
                {
                    errors.Add(new WireframeParseError(lineNumber, "A wireframe begins with a 'screen' line without indentation."));
                    if (screens.Count == 0)
                    {
                        return new WireframeParseResult(screens, errors, viewport);
                    }

                    continue;
                }

                screens.Add(node);
            }
            else if (parent.IsContainer)
            {
                // AC-914: a state stands for a whole screen's variant, so it belongs directly under one — anywhere
                // else it would be a container inside a container with no screen of its own to have replaced from.
                if (node.Kind == WireframeNodeKind.State && parent.Kind != WireframeNodeKind.Screen)
                {
                    errors.Add(new WireframeParseError(lineNumber, "A 'state' can only be a direct child of a screen."));
                    continue;
                }

                parent.Children.Add(node);
            }
            else
            {
                var keyword = parent.Kind.ToString().ToLowerInvariant();
                errors.Add(new WireframeParseError(lineNumber, $"'{keyword}' cannot carry child lines."));
                continue;
            }

            open.Add((indent, node));
        }

        // AC-902: `goto:` names a screen by title, and screens can be declared after the component that points at
        // one — so this cannot run inside _Validate, which reads one line at a time.
        foreach (var screen in screens)
        {
            _ValidateGotoTargets(screen, screens, errors);
            // AC-914: a state can equally be declared above or below the container it names — read the whole
            // screen first, then resolve `replaces:` against it.
            _ValidateReplacesTargets(screen, errors);
        }

        return new WireframeParseResult(screens, errors, viewport);
    }

    private static readonly Dictionary<string, WireframeViewport> _ViewportNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["desktop"] = WireframeViewport.Desktop,
        ["tablet"] = WireframeViewport.Tablet,
        ["mobile"] = WireframeViewport.Mobile,
    };

    // AC-915: "viewport" as a whole word at the start of the line — not a node keyword, so this runs ahead of
    // _ReadNode rather than through it. `after` is the position right past the keyword, for the caller to read the
    // name from.
    private static bool _TryReadViewportKeyword(string content, out int after)
    {
        var position = 0;
        var word = _ReadWord(content, ref position);
        after = position;
        return string.Equals(word, "viewport", StringComparison.Ordinal) && (position == content.Length || content[position] == ' ');
    }

    private static void _ValidateGotoTargets(WireframeNode node, IReadOnlyList<WireframeNode> screens, List<WireframeParseError> errors)
    {
        if (node.ValueOf(WireframeModifierName.Goto) is { } title && WireframeGotoResolver.Resolve(screens, title).Error is { } error)
        {
            errors.Add(new WireframeParseError(node.Line, error));
        }

        foreach (var child in node.Children)
        {
            _ValidateGotoTargets(child, screens, errors);
        }
    }

    // AC-914: `replaces:#<id>` resolved against this state's own screen only — ids are document-unique, but a state
    // stands in for a container of the screen it lives on, never one belonging to another.
    private static void _ValidateReplacesTargets(WireframeNode screen, List<WireframeParseError> errors)
    {
        foreach (var state in screen.Children.Where(child => child.Kind == WireframeNodeKind.State))
        {
            if (state.ValueOf(WireframeModifierName.Replaces) is not { } value)
            {
                errors.Add(new WireframeParseError(state.Line, "A 'state' needs replaces:#<id> — the container whose contents it stands in for."));
                continue;
            }

            var target = WireframeHandEdit.Find(screen, value.TrimStart('#'));
            if (target is null)
            {
                errors.Add(new WireframeParseError(state.Line, $"'{value}' is not a component of this screen."));
            }
            else if (!target.IsContainer)
            {
                errors.Add(new WireframeParseError(state.Line, $"'{value}' is not a container, so a state cannot replace what is inside it."));
            }
            else if (target == screen || target.Kind == WireframeNodeKind.State)
            {
                errors.Add(new WireframeParseError(state.Line, "A state replaces a container inside its screen, not the screen itself."));
            }
        }
    }

    private static WireframeNode? _ReadNode(string content, int lineNumber, List<WireframeParseError> errors)
    {
        var position = 0;
        var word = _ReadWord(content, ref position);
        if (word.Length == 0
            || (position < content.Length && content[position] != ' ')
            || !Enum.TryParse<WireframeNodeKind>(word, ignoreCase: true, out var kind))
        {
            errors.Add(new WireframeParseError(lineNumber, $"Unknown component '{_TokenAt(content, 0)}'."));
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
                    errors.Add(new WireframeParseError(lineNumber, "The quote mark is not closed."));
                    return null;
                }

                if (node.Text is not null || node.Id is not null || node.Modifiers.Count > 0)
                {
                    errors.Add(new WireframeParseError(lineNumber, "The text must come directly after the component."));
                    return null;
                }

                node.Text = text;
                continue;
            }

            if (content[position] == '#')
            {
                if (!_TryReadId(content, ref position, out var id))
                {
                    errors.Add(new WireframeParseError(lineNumber, "An id consists of letters, digits, '-' and '_', like '#save-btn'."));
                    return null;
                }

                if (node.Id is not null)
                {
                    errors.Add(new WireframeParseError(lineNumber, "A component carries at most one id."));
                    return null;
                }

                node.Id = id;
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
            errors.Add(new WireframeParseError(lineNumber, $"Unknown modifier '{written}'."));
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
                    errors.Add(new WireframeParseError(lineNumber, "The quote mark is not closed."));
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
            or WireframeModifierName.Value
            or WireframeModifierName.Goto
            or WireframeModifierName.Note
            or WireframeModifierName.Replaces;

        if (takesValue && string.IsNullOrEmpty(value))
        {
            // AC-907/AC-914: a numeric or quoted example is nonsense for a modifier whose value is a weight or an id.
            var hint = name switch
            {
                WireframeModifierName.W or WireframeModifierName.H => $"{written}:2",
                WireframeModifierName.Replaces => $"{written}:#<id>",
                _ => $"{written}:\"…\"",
            };
            errors.Add(new WireframeParseError(lineNumber, $"'{written}' needs a value, like '{hint}'."));
            return null;
        }

        if (!takesValue && value is not null)
        {
            errors.Add(new WireframeParseError(lineNumber, $"'{written}' does not take a value."));
            return null;
        }

        switch (name)
        {
            case WireframeModifierName.W or WireframeModifierName.H
                when !int.TryParse(value, out var weight) || weight <= 0:
                errors.Add(new WireframeParseError(lineNumber, $"The weight for '{written}' must be a positive whole number."));
                return null;
            case WireframeModifierName.Align
                when !Enum.TryParse<WireframeAlignment>(value, ignoreCase: true, out _):
                errors.Add(new WireframeParseError(lineNumber, $"Unknown alignment '{value}' — choose left, center or right."));
                return null;
            default:
                return new WireframeModifier(name, value, isQuoted);
        }
    }

    // `#save-btn` (AC-906): everything up to the next space, and only from the alphabet an id may use — so a stray
    // `#` or a quote inside one is a refusal rather than a component nobody can name back.
    private static bool _TryReadId(string content, ref int position, out string id)
    {
        var start = ++position;
        while (position < content.Length && content[position] != ' ')
        {
            position++;
        }

        id = content[start..position];
        return id.Length > 0 && id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
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
