using System.Globalization;
using System.Xml.Linq;
using Mermaider;
using Mermaider.Models;

namespace Cockpit.Infrastructure.Diagrams;

/// <summary>
/// The one public seam between Mermaid text and a normalized SVG (AC-807, sub of the AC-525 diagram
/// builder epic). Everything Mermaider-specific — its renderer, its <c>RenderOptions</c>, its raw CSS
/// custom-property output — stays behind this class; nothing outside it references a Mermaider type, and
/// its own output carries no unresolved <c>var()</c>/<c>color-mix()</c> for a downstream SVG consumer
/// (Svg.Skia or otherwise) to trip over.
/// </summary>
public static class MermaidRenderPipeline
{
    // Mermaider lays out text against Inter's own metrics; render with anything else and labels drift
    // outside the node/arrow bounds they were measured for. The host already carries Avalonia.Fonts.Inter,
    // so it is always present — no fallback chain needed.
    private const string PinnedFont = "Inter";

    public static SvgDocument Render(string source, MermaidTheme theme)
    {
        var svg = MermaidRenderer.RenderSvg(source, new RenderOptions
        {
            Bg = theme.Bg,
            Fg = theme.Fg,
            Line = theme.Line,
            Accent = theme.Accent,
            Muted = theme.Muted,
            Surface = theme.Surface,
            Border = theme.Border,
            Font = PinnedFont,
            FontSize = $"{theme.FontSizePx.ToString(CultureInfo.InvariantCulture)}px",
        });

        var flattened = CssFlattener.Flatten(svg);
        var (width, height) = ReadViewport(flattened);
        return new SvgDocument(flattened, width, height);
    }

    private static (double Width, double Height) ReadViewport(string svg)
    {
        var root = XDocument.Parse(svg).Root;
        var width = ParseLength(root?.Attribute("width")?.Value);
        var height = ParseLength(root?.Attribute("height")?.Value);
        if (width is null || height is null)
        {
            var viewBox = root?.Attribute("viewBox")?.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (viewBox is { Length: 4 })
            {
                width ??= double.Parse(viewBox[2], CultureInfo.InvariantCulture);
                height ??= double.Parse(viewBox[3], CultureInfo.InvariantCulture);
            }
        }

        return (width ?? 0, height ?? 0);
    }

    private static double? ParseLength(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var numeric = value.EndsWith("px", StringComparison.OrdinalIgnoreCase) ? value[..^2] : value;
        return double.TryParse(numeric, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }
}
