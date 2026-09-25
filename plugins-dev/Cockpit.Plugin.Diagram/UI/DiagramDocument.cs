namespace Cockpit.Plugin.Diagram;

// The diagram a window stands for (AC-834). `Id` is the window's key and the AC-810 registry's surface id: a
// saved diagram's AC-812 file path, or a fresh id when it has none yet. `FilePath` null means "No file yet"
// (AC-839), until the first save turns it into one.
internal sealed record DiagramDocument(string Id, string Title, string MermaidText, string? FilePath = null)
{
    // A valid, node-less flowchart (AC-840): renders as a blank canvas, and is what "add node" builds on top of.
    public const string Empty = "flowchart LR";

    // AC-911: opens with whatever template it was given — Empty by default, so existing call-sites (whiteboard→
    // diagram, DiagramWindowTests) keep working unchanged. The AC-809 sample is gone; DiagramTemplates.Flowchart
    // takes its role, reachable through the same template list as "Insert template…" on the panel.
    public static DiagramDocument New(string title, string source = Empty) => new(Guid.NewGuid().ToString("n"), title, source);
}
