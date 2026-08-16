namespace Cockpit.Plugin.Diagram;

// Hands a diagram picked in the list (AC-826) to the next diagram.panel body that gets created — a single
// UI-thread slot is enough since opening a workspace and building its body happen back-to-back there, never
// interleaved. Cleared by the body that consumes it, so a later blank "Diagram Builder" open still gets the sample.
internal static class DiagramOpenHandoff
{
    public static (string Title, string MermaidText)? Pending { get; set; }
}
