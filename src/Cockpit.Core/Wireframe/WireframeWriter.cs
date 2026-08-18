using System.Text;
using Cockpit.Core.Wireframe.Model;

namespace Cockpit.Core.Wireframe;

// The way back from tree to source text (AC-871). Two spaces per level is the canonical form the docs describe,
// so parse-then-write on a canonical source gives the same text back character for character.
public static class WireframeWriter
{
    public static string Write(WireframeNode root)
    {
        var builder = new StringBuilder();
        _Write(root, 0, builder);
        return builder.ToString().TrimEnd('\n');
    }

    // One node as a single source line, indented by `indent` spaces and without its children.
    public static string Line(WireframeNode node, int indent)
    {
        var builder = new StringBuilder();
        _WriteLine(node, indent, builder);
        return builder.ToString();
    }

    private static void _Write(WireframeNode node, int depth, StringBuilder builder)
    {
        _WriteLine(node, depth * 2, builder);
        builder.Append('\n');

        foreach (var child in node.Children)
        {
            _Write(child, depth + 1, builder);
        }
    }

    private static void _WriteLine(WireframeNode node, int indent, StringBuilder builder)
    {
        builder.Append(' ', indent).Append(node.Kind.ToString().ToLowerInvariant());

        if (node.Text is not null)
        {
            builder.Append(" \"").Append(_Escape(node.Text)).Append('"');
        }

        foreach (var modifier in node.Modifiers)
        {
            builder.Append(' ').Append(modifier.Name.ToString().ToLowerInvariant());
            if (modifier.Value is null)
            {
                continue;
            }

            builder.Append(':');
            if (modifier.IsQuoted)
            {
                builder.Append('"').Append(_Escape(modifier.Value)).Append('"');
            }
            else
            {
                builder.Append(modifier.Value);
            }
        }

        // Last on the line, where it stays out of the way of the wording the operator actually reads (AC-906).
        if (node.Id is { } id)
        {
            builder.Append(" #").Append(id);
        }
    }

    // One piece of text as the source spells it: double-quoted, with its own quotes and backslashes escaped.
    public static string Quote(string text) => $"\"{_Escape(text)}\"";

    private static string _Escape(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
