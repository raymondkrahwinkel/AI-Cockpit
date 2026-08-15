namespace Cockpit.Infrastructure.Diagrams;

// The normalized output of MermaidRenderPipeline: plain SVG markup with every var()/color-mix() flattened
// and rem converted to px, ready for any SVG consumer (Svg.Skia or otherwise) without that consumer needing
// to know Mermaider exists.
public sealed record SvgDocument(string Markup, double Width, double Height);
