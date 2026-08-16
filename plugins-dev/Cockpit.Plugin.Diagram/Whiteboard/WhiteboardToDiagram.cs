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
            return "Deze cockpit tekent geen diagrammen — omzetten kan hier niet.";
        }

        if (!sessionLive || coupling is null)
        {
            return "Geen agent gekoppeld — koppel eerst een sessie, dan kan hij het bord omzetten.";
        }

        return coupling.CanRead
            ? null
            : "De agent mag dit bord niet lezen — laat hem eerst meekijken, anders heeft hij niets om om te zetten.";
    }

    // De statusregel onder het bord: wat er gevraagd is, en wat er echt in de poort is geland.
    public static string Status(bool asked, int proposals) => proposals switch
    {
        0 when !asked => "",
        0 => "Omzetting gevraagd — wacht op een voorstel in de diff-poort.",
        1 => "1 omzetting voorgesteld",
        _ => $"{proposals} omzettingen voorgesteld",
    };

    // Names the target by id, not by name: two windows can carry the same title, and Resolve takes either.
    public static string ConvertPrompt(string boardName, string diagramId, string diagramName) =>
        $"""
        Zet het whiteboard "{boardName}" om naar een diagram.

        Lees het bord met read_whiteboard en stel de omzetting daarna in één keer voor met edit_diagram op diagram-id {diagramId} ("{diagramName}"): de hele Mermaid-bron als één voorstel.

        Gebruik hiervoor niet add_node, rename_node, remove_node, connect_nodes of disconnect_nodes — die schrijven direct in het diagram. Een omzetting hoort als voorstel in de diff-poort te landen, zodat de operator hem blok voor blok kan aannemen of afwijzen en er niets stilzwijgend overschreven wordt. Verander zelf niets op het bord.
        """;

    public static string WriteDownPrompt(string boardName) =>
        $"""
        Lees het whiteboard "{boardName}" met read_whiteboard en schrijf in dit gesprek op wat erop staat: de vormen, de teksten en hoe ze samenhangen.

        Zet het niet om naar een diagram en verander niets — niet op het bord en niet in een diagram.
        """;
}
