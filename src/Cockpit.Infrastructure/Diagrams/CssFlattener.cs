using System.Globalization;
using System.Text.RegularExpressions;

namespace Cockpit.Infrastructure.Diagrams;

// Resolves CSS custom properties and color-mix() in Mermaider's SVG output, which Svg.Skia cannot parse
// (unresolved uses fall back to no stroke, black fill). Uses paren-depth tracking instead of a regex, since
// a regex cannot match balanced/nested calls like color-mix(in srgb, var(--accent, var(--fg)) 8%, var(--bg)).
internal static partial class CssFlattener
{
    private const double RootFontSizePx = 16;

    public static string Flatten(string svg)
    {
        var customProperties = ParseRootCustomProperties(svg);

        // Property values can themselves reference other properties or color-mix() (e.g. the `--_group-fill`
        // family) — resolve those first so substituting them into the document is then a single further pass.
        foreach (var name in customProperties.Keys.ToList())
        {
            customProperties[name] = ResolveFunctions(customProperties[name], customProperties);
        }

        var flattened = ResolveFunctions(svg, customProperties);
        return ConvertRemToPx(flattened);
    }

    // Mermaider 0.12.2 never emits a :root selector; base roles live on the root <svg>'s style="" attribute
    // and derived ones in a `svg { }` rule. :root is still parsed for a future Mermaider version or
    // hand-authored source, though it is dead against today's output.
    private static Dictionary<string, string> ParseRootCustomProperties(string svg)
    {
        var properties = new Dictionary<string, string>();
        ParseAttributeDeclarations(svg, properties);
        ParseRuleDeclarations(svg, ":root", properties);
        ParseRuleDeclarations(svg, "svg", properties);
        return properties;
    }

    private static void ParseAttributeDeclarations(string svg, Dictionary<string, string> properties)
    {
        var svgTagStart = svg.IndexOf("<svg", StringComparison.Ordinal);
        if (svgTagStart < 0)
        {
            return;
        }

        var svgTagEnd = svg.IndexOf('>', svgTagStart);
        if (svgTagEnd < 0)
        {
            return;
        }

        var styleStart = svg.IndexOf("style=\"", svgTagStart, StringComparison.Ordinal);
        if (styleStart < 0 || styleStart > svgTagEnd)
        {
            return;
        }

        styleStart += "style=\"".Length;
        var styleEnd = svg.IndexOf('"', styleStart);
        if (styleEnd < 0 || styleEnd > svgTagEnd)
        {
            return;
        }

        ParseDeclarations(svg[styleStart..styleEnd], properties);
    }

    // Finds `selector {` as a standalone rule (not, say, the "svg" inside the root <svg ...> tag's own name)
    // by requiring the next non-whitespace character after the selector text to be the opening brace.
    private static void ParseRuleDeclarations(string svg, string selector, Dictionary<string, string> properties)
    {
        var searchFrom = 0;
        while (true)
        {
            var selectorStart = svg.IndexOf(selector, searchFrom, StringComparison.Ordinal);
            if (selectorStart < 0)
            {
                return;
            }

            var braceStart = selectorStart + selector.Length;
            while (braceStart < svg.Length && char.IsWhiteSpace(svg[braceStart]))
            {
                braceStart++;
            }

            if (braceStart < svg.Length && svg[braceStart] == '{')
            {
                var braceEnd = FindMatching(svg, braceStart, '{', '}');
                if (braceEnd >= 0)
                {
                    ParseDeclarations(svg[(braceStart + 1)..braceEnd], properties);
                }

                return;
            }

            searchFrom = selectorStart + selector.Length;
        }
    }

    private static void ParseDeclarations(string block, Dictionary<string, string> properties)
    {
        foreach (var declaration in SplitTopLevel(block, ';'))
        {
            var colon = declaration.IndexOf(':');
            if (colon < 0)
            {
                continue;
            }

            var name = declaration[..colon].Trim();
            var value = declaration[(colon + 1)..].Trim();
            if (name.StartsWith("--", StringComparison.Ordinal) && value.Length > 0)
            {
                properties[name] = value;
            }
        }
    }

    // Repeatedly replaces the innermost var()/color-mix() call — one whose argument list contains no
    // further var(/color-mix( of its own — until none remain, so nested calls resolve from the inside out.
    private static string ResolveFunctions(string text, Dictionary<string, string> customProperties)
    {
        // A generous, finite bound: real nesting here is at most a handful of levels deep. This only
        // guards against a malformed/cyclic document looping forever, not a case seen in practice.
        for (var guard = 0; guard < 500; guard++)
        {
            var call = FindInnermostCall(text);
            if (call is null)
            {
                return text;
            }

            var (start, end, name, args) = call.Value;
            var replacement = name == "var" ? ResolveVar(args, customProperties) : ResolveColorMix(args);
            text = string.Concat(text.AsSpan(0, start), replacement, text.AsSpan(end));
        }

        return text;
    }

    private static (int Start, int End, string Name, string Args)? FindInnermostCall(string text)
    {
        var searchFrom = 0;
        while (true)
        {
            var varIndex = text.IndexOf("var(", searchFrom, StringComparison.Ordinal);
            var mixIndex = text.IndexOf("color-mix(", searchFrom, StringComparison.Ordinal);
            if (varIndex < 0 && mixIndex < 0)
            {
                return null;
            }

            int start, openParen;
            string name;
            if (mixIndex < 0 || (varIndex >= 0 && varIndex < mixIndex))
            {
                start = varIndex;
                name = "var";
                openParen = varIndex + 3;
            }
            else
            {
                start = mixIndex;
                name = "color-mix";
                openParen = mixIndex + "color-mix".Length;
            }

            var end = FindMatching(text, openParen, '(', ')');
            if (end < 0)
            {
                return null; // malformed; stop rather than loop forever
            }

            var args = text[(openParen + 1)..end];
            if (!args.Contains("var(", StringComparison.Ordinal) && !args.Contains("color-mix(", StringComparison.Ordinal))
            {
                return (start, end + 1, name, args);
            }

            // This call has a nested call inside its own argument list — look further in for the innermost one.
            searchFrom = openParen + 1;
        }
    }

    private static int FindMatching(string text, int openIndex, char open, char close)
    {
        var depth = 0;
        for (var i = openIndex; i < text.Length; i++)
        {
            if (text[i] == open)
            {
                depth++;
            }
            else if (text[i] == close && --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static List<string> SplitTopLevel(string text, char separator)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '(' or '{':
                    depth++;
                    break;
                case ')' or '}':
                    depth--;
                    break;
                default:
                    if (text[i] == separator && depth == 0)
                    {
                        parts.Add(text[start..i]);
                        start = i + 1;
                    }
                    break;
            }
        }

        if (start < text.Length)
        {
            parts.Add(text[start..]);
        }

        return parts;
    }

    // var(--name) or var(--name, fallback) — by the time this runs, a fallback that was itself a function
    // call has already been resolved (FindInnermostCall only ever hands back a leaf call).
    private static string ResolveVar(string args, Dictionary<string, string> customProperties)
    {
        var parts = SplitTopLevel(args, ',').ConvertAll(p => p.Trim());
        var name = parts[0];
        if (customProperties.TryGetValue(name, out var value))
        {
            return value;
        }

        if (parts.Count > 1)
        {
            return parts[1];
        }

        // No declared value and no var() fallback: guessing a color here is exactly the silent-failure
        // class AC-808 exists to catch, so this fails loudly instead of guessing #000000.
        throw new InvalidOperationException(
            $"CssFlattener: custom property '{name}' has no declared value and no var() fallback.");
    }

    // color-mix(in srgb, colorA [p1%], colorB [p2%]) — colorspace keyword ignored, channels blended linearly.
    // ponytail: sRGB channel lerp, not perceptual mixing — upgrade to true CSS Color 4 mixing if a theme
    // ever visibly bands.
    private static string ResolveColorMix(string args)
    {
        var stops = SplitTopLevel(args, ',').ConvertAll(p => p.Trim()).Skip(1).Select(ParseColorStop).ToList();
        if (stops.Count < 2)
        {
            return stops.Count == 1 ? ToHex(stops[0].Color) : "#000000";
        }

        var (color1, p1) = stops[0];
        var (color2, p2) = stops[1];
        var weight1 = p1 ?? (p2.HasValue ? 100 - p2.Value : 50);
        var weight2 = p2 ?? 100 - weight1;
        var total = weight1 + weight2 is > 0 ? weight1 + weight2 : 100;

        var r = (byte)Math.Round((color1.R * weight1 + color2.R * weight2) / total);
        var g = (byte)Math.Round((color1.G * weight1 + color2.G * weight2) / total);
        var b = (byte)Math.Round((color1.B * weight1 + color2.B * weight2) / total);
        return ToHex((r, g, b));
    }

    private static ((byte R, byte G, byte B) Color, double? Percent) ParseColorStop(string stop)
    {
        var tokens = stop.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var color = ParseColor(tokens[0]);
        double? percent = null;
        if (tokens.Length > 1 && tokens[1].EndsWith('%') && double.TryParse(tokens[1][..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var p))
        {
            percent = p;
        }

        return (color, percent);
    }

    private static (byte R, byte G, byte B) ParseColor(string token)
    {
        token = token.Trim();
        if (token.StartsWith('#'))
        {
            var hex = token[1..];
            if (hex.Length == 3)
            {
                return (
                    Convert.ToByte(new string(hex[0], 2), 16),
                    Convert.ToByte(new string(hex[1], 2), 16),
                    Convert.ToByte(new string(hex[2], 2), 16));
            }

            if (hex.Length is 6 or 8)
            {
                return (Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..6], 16));
            }
        }

        return token switch
        {
            "white" => (255, 255, 255),
            _ => (0, 0, 0), // "black", "transparent", or anything unrecognized
        };
    }

    private static string ToHex((byte R, byte G, byte B) color) => $"#{color.R:x2}{color.G:x2}{color.B:x2}";

    [GeneratedRegex(@"(-?\d*\.?\d+)rem")]
    private static partial Regex RemPattern();

    private static string ConvertRemToPx(string text) =>
        RemPattern().Replace(text, m =>
        {
            var px = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * RootFontSizePx;
            return $"{px.ToString("0.###", CultureInfo.InvariantCulture)}px";
        });
}
