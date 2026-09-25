namespace Cockpit.Plugin.Diagram;

// The diagram a window stands for (AC-834). `Id` is what the window keys on — one window per document, never per
// session — so it has to be the diagram's own stable identity: AC-812's file path for a saved one, a fresh id for
// a diagram that has no file yet. It doubles as the surface id the AC-810 registry knows this diagram by.
// `FilePath` is where it already lives (AC-839); null means it has no file yet, which the window draws as
// "No file yet" and the first save turns into one.
internal sealed record DiagramDocument(string Id, string Title, string MermaidText, string? FilePath = null)
{
    // A valid, node-less flowchart (AC-840): renders as a blank canvas, and is what "add node" builds on top of.
    public const string Empty = "flowchart LR";

    // AC-911: opens with whatever template it was given — Empty by default, so existing call-sites (whiteboard→
    // diagram, DiagramWindowTests) keep working unchanged. The AC-809 sample is gone; DiagramTemplates.Flowchart
    // takes its role, reachable through the same template list as "Insert template…" on the panel.
    public static DiagramDocument New(string title, string source = Empty) => new(Guid.NewGuid().ToString("n"), title, source);
}
