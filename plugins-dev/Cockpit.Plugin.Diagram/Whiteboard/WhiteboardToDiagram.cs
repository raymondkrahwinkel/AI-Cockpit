using Cockpit.Core.Abstractions.Whiteboard;

namespace Cockpit.Plugin.Diagram.Whiteboard;

// W-4/AC-845: the one place the diff-poort (AC-825) still stands — a whole board turned into a whole diagram is a
// proposal, not an edit. The turn the cockpit sends is written here rather than by the agent, and names
// edit_diagram on purpose: the per-object tools (AC-852) write straight through, past the poort.
internal static class WhiteboardToDiagram
{
    // Why converting is off right now, or null when it can be offered.
    public static string? Blocker(bool hasDiagramSurfaces, bool sessionLive, WhiteboardCoupling? coupling)
    {
        if (!hasDiagramSurfaces)
        {
            return "This cockpit does not draw diagrams — converting is not possible here.";
        }

        if (!sessionLive || coupling is null)
        {
            return "No agent coupled — couple a session first, then it can convert the board.";
        }

        return coupling.CanRead
            ? null
            : "The agent may not read this board — let it look along first, otherwise it has nothing to convert.";
    }

    // The status line below the board: what was asked, and what actually landed in the diff gate.
    public static string Status(bool asked, int proposals) => proposals switch
    {
        0 when !asked => "",
        0 => "Conversion requested — waiting for a proposal in the diff gate.",
        1 => "1 conversion proposed",
        _ => $"{proposals} conversions proposed",
    };

    // Names the target by id, not by name: two windows can carry the same title, and Resolve takes either.
    public static string ConvertPrompt(string boardName, string diagramId, string diagramName) =>
        $"""
        Convert the whiteboard "{boardName}" to a diagram.

        Read the board with read_whiteboard and then propose the conversion in one go with edit_diagram on diagram id {diagramId} ("{diagramName}"): the whole Mermaid source as one proposal.

        Do not use add_node, rename_node, remove_node, connect_nodes or disconnect_nodes for this — those write straight to the diagram. A conversion belongs in the diff gate as a proposal, so the operator can accept or reject it block by block and nothing gets silently overwritten. Do not change anything on the board yourself.
        """;

    public static string WriteDownPrompt(string boardName) =>
        $"""
        Read the whiteboard "{boardName}" with read_whiteboard and write down in this conversation what is on it: the shapes, the texts and how they relate.

        Do not convert it to a diagram and do not change anything — not on the board and not in a diagram.
        """;
}
