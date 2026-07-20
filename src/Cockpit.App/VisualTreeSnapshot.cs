using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Cockpit.App;

// Serialises a rendered visual tree to indented text: per control its type, name, absolute bounds,
// text content and resolved brushes. This is the provider-neutral half of the verify loop (AC-86) —
// every session (Claude, Codex, local LLM) can read text, where only vision models can read the PNG.
// The numbers are exact, so an agent verifies layout facts (overflow, colour, alignment) it cannot see.
internal static class VisualTreeSnapshot
{
    // A whole window is thousands of nodes; keep the dump bounded and say so when it is cut off.
    private const int DefaultMaxDepth = 40;
    private const int NodeLimit = 2000;

    // A named target scopes the snapshot to that control's subtree — the verify loop cares about the one
    // control that was just changed, not the whole window. Falls back to the full tree when unnamed or unfound.
    public static string Capture(Visual root, string? targetName = null, int maxDepth = DefaultMaxDepth)
    {
        var builder = new StringBuilder();

        var start = root;
        if (!string.IsNullOrEmpty(targetName))
        {
            // Match an x:Name first, then a control type name — a changed control usually has no name but its
            // type (e.g. SessionUsagePill) is what the agent knows from the XAML file it just edited.
            var found = root.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == targetName)
                ?? root.GetVisualDescendants().FirstOrDefault(v => v.GetType().Name == targetName);
            if (found is null)
            {
                builder.Append(CultureInfo.InvariantCulture, $"(no control named or typed \"{targetName}\" — showing full tree)");
                builder.AppendLine();
            }
            else
            {
                start = found;
            }
        }

        var written = 0;
        var parentOrigin = _AbsoluteOrigin(start) - start.Bounds.Position;
        var truncated = _Walk(start, parentOrigin, 0, maxDepth, builder, ref written);

        if (truncated)
        {
            builder.Append(CultureInfo.InvariantCulture, $"… truncated at {NodeLimit} nodes");
            builder.AppendLine();
        }

        return builder.ToString();
    }

    // Sums this visual's offset and every ancestor's, giving the top-left in window coordinates.
    private static Point _AbsoluteOrigin(Visual visual)
    {
        var origin = new Point(0, 0);
        for (Visual? node = visual; node is not null; node = node.GetVisualParent())
        {
            origin += node.Bounds.Position;
        }

        return origin;
    }

    private static bool _Walk(Visual visual, Point parentOrigin, int depth, int maxDepth, StringBuilder builder, ref int written)
    {
        if (written >= NodeLimit)
        {
            return true;
        }

        // A hidden or zero-area subtree renders nothing, so it is noise in a layout snapshot — skip it whole.
        if (!visual.IsVisible || visual.Bounds.Width <= 0 || visual.Bounds.Height <= 0)
        {
            return false;
        }

        var origin = parentOrigin + visual.Bounds.Position;
        _AppendNode(visual, origin, depth, builder);
        written++;

        if (depth >= maxDepth)
        {
            // Mark rather than silently drop, so the agent can tell a subtree was omitted, not that it is absent.
            if (visual.GetVisualChildren().Any())
            {
                builder.Append(' ', (depth + 1) * 2);
                builder.Append("… (depth-capped)");
                builder.AppendLine();
            }

            return false;
        }

        foreach (var child in visual.GetVisualChildren())
        {
            if (_Walk(child, origin, depth + 1, maxDepth, builder, ref written))
            {
                return true;
            }
        }

        return false;
    }

    private static void _AppendNode(Visual visual, Point origin, int depth, StringBuilder builder)
    {
        builder.Append(' ', depth * 2);
        builder.Append(visual.GetType().Name);

        if (visual is Control { Name: { Length: > 0 } name })
        {
            builder.Append(CultureInfo.InvariantCulture, $" \"{name}\"");
        }

        var bounds = visual.Bounds;
        builder.Append(CultureInfo.InvariantCulture,
            $"  {_Round(origin.X)},{_Round(origin.Y)} {_Round(bounds.Width)}×{_Round(bounds.Height)}");

        _AppendText(visual, builder);
        _AppendBrushes(visual, builder);
        _AppendCorner(visual, builder);

        builder.AppendLine();
    }

    private static void _AppendText(Visual visual, StringBuilder builder)
    {
        var text = visual switch
        {
            TextBlock { Text: { Length: > 0 } t } => t,
            TextBox { Text: { Length: > 0 } t } => t,
            _ => null,
        };

        if (text is not null)
        {
            builder.Append(CultureInfo.InvariantCulture, $" \"{text}\"");
        }
    }

    private static void _AppendBrushes(Visual visual, StringBuilder builder)
    {
        var (background, foreground) = visual switch
        {
            Border border => (border.Background, null),
            TemplatedControl templated => (templated.Background, templated.Foreground),
            TextBlock textBlock => (null, textBlock.Foreground),
            _ => (null, (IBrush?)null),
        };

        if (_Hex(background) is { } bg)
        {
            builder.Append(CultureInfo.InvariantCulture, $" bg={bg}");
        }

        if (_Hex(foreground) is { } fg)
        {
            builder.Append(CultureInfo.InvariantCulture, $" fg={fg}");
        }
    }

    private static void _AppendCorner(Visual visual, StringBuilder builder)
    {
        var radius = visual switch
        {
            Border border => border.CornerRadius,
            TemplatedControl templated => templated.CornerRadius,
            _ => default,
        };

        if (!radius.Equals(default(CornerRadius)))
        {
            builder.Append(CultureInfo.InvariantCulture, $" corner={_Round(radius.TopLeft)}");
        }
    }

    // Only solid brushes resolve to a colour worth verifying; gradients/none are left off rather than
    // described vaguely, so what appears in the snapshot is always a fact the agent can check.
    private static string? _Hex(IBrush? brush)
        => brush is ISolidColorBrush { Color: var color }
            ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
            : null;

    private static int _Round(double value) => (int)Math.Round(value);
}
