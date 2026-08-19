using Cockpit.Core.Abstractions.Diagrams;

namespace Cockpit.Infrastructure.Diagrams;

// The pipeline's whole answer. Fidelity travels with the SVG rather than beside it so a caller cannot get a
// picture out of this pipeline without also being handed what is missing from it — leaving the report
// optional would reinstate exactly the silence AC-808 exists to end.
public sealed record MermaidRenderResult(SvgDocument Svg, DiagramFidelity Fidelity);
