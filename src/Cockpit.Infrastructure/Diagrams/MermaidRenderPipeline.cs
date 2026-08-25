using System.Globalization;
using System.Xml.Linq;
using Mermaider;
using Mermaider.Models;

namespace Cockpit.Infrastructure.Diagrams;

// The one public seam between Mermaid text and a normalized SVG (AC-807). Everything Mermaider-specific
// stays behind this class, and its output carries no unresolved var()/color-mix() for a downstream SVG
// consumer to trip over. Also answers for what the engine dropped (AC-808): a render always returns the picture and fidelity report together.
public static class MermaidRenderPipeline
{
    // Mermaider lays out text against Inter's own metrics; render with anything else and labels drift
    // outside the node/arrow bounds they were measured for. The host already carries Avalonia.Fonts.Inter,
    // so it is always present — no fallback chain needed.
    private const string PinnedFont = "Inter";

    public static MermaidRenderResult Render(string source, MermaidTheme theme)
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
        return new MermaidRenderResult(
            new SvgDocument(flattened, width, height),
            FidelityCheck.Check(source, flattened));
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
