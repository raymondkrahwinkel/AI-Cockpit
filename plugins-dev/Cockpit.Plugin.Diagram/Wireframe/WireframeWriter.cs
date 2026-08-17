using System.Text;
using Cockpit.Plugin.Diagram.Wireframe.Model;

namespace Cockpit.Plugin.Diagram.Wireframe;

// The way back from tree to source text (AC-871). Two spaces per level is the canonical form the docs describe,
// so parse-then-write on a canonical source gives the same text back character for character.
internal static class WireframeWriter
{
    public static string Write(WireframeNode root)
    {
        var builder = new StringBuilder();
        _Write(root, 0, builder);
        return builder.ToString().TrimEnd('\n');
    }

    private static void _Write(WireframeNode node, int depth, StringBuilder builder)
    {
        builder.Append(' ', depth * 2).Append(node.Kind.ToString().ToLowerInvariant());

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

        builder.Append('\n');

        foreach (var child in node.Children)
        {
            _Write(child, depth + 1, builder);
        }
    }

    private static string _Escape(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
