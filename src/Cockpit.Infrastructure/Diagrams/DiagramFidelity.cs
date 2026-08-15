namespace Cockpit.Infrastructure.Diagrams;

// What the render engine dropped on the floor (AC-808). Mermaider can leave a construct out of its SVG
// without throwing, warning, or leaving a gap — the picture looks complete and says something other than
// the source does, which is worse than a visible failure because a decision gets taken on it. Every finding
// is a finished sentence, so both consumers of a render — the operator's surface and the agent's MCP reply
// — say the same thing without each inventing its own phrasing.
public sealed record DiagramFidelity(IReadOnlyList<string> Findings)
{
    public bool IsComplete => Findings.Count == 0;
}

// The pipeline's whole answer. Fidelity travels with the SVG rather than beside it so a caller cannot get a
// picture out of this pipeline without also being handed what is missing from it — leaving the report
// optional would reinstate exactly the silence AC-808 exists to end.
public sealed record MermaidRenderResult(SvgDocument Svg, DiagramFidelity Fidelity);
