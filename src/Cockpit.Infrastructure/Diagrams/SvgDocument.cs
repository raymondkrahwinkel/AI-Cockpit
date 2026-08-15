namespace Cockpit.Infrastructure.Diagrams;

/// <summary>
/// The normalized output of <see cref="MermaidRenderPipeline"/>: plain SVG markup with every
/// <c>var()</c>/<c>color-mix()</c> flattened and <c>rem</c> converted to <c>px</c>, ready for any SVG
/// consumer (Svg.Skia or otherwise) without that consumer needing to know Mermaider exists.
/// </summary>
public sealed record SvgDocument(string Markup, double Width, double Height);
