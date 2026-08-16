namespace Cockpit.Plugin.Diagram;

// The diagram a window stands for (AC-834). `Id` is what the window keys on — one window per document, never per
// session — so it has to be the diagram's own stable identity: AC-812's file path for a saved one, a fresh id for
// a diagram that has no file yet. It doubles as the surface id the AC-810 registry knows this diagram by.
// `FilePath` is where it already lives (AC-839); null means it has no file yet, which the window draws as
// "Nog geen bestand" and the first save turns into one.
internal sealed record DiagramDocument(string Id, string Title, string MermaidText, string? FilePath = null)
{
    // A valid, node-less flowchart (AC-840): renders as a blank canvas, and is what "voorbeeld invoegen" and
    // "node toevoegen" both build on top of.
    public const string Empty = "flowchart LR";

    public const string Sample = """
        flowchart LR
            Zip[Plugin zip] -->|PluginLoadContext| Fallthrough{Falls through?}
            Fallthrough -->|Avalonia, Skia| Host[Host's own copy]
            Fallthrough -->|Mermaider| Own[Plugin's own copy]
            Host --> Panel[This panel]
            Own --> Panel
        """;

    // AC-840: a quick-started diagram opens empty, named for what the operator asked for — never the AC-809
    // sample, which is now only reachable via the panel's explicit "Voorbeeld invoegen" action.
    public static DiagramDocument New(string title) => new(Guid.NewGuid().ToString("n"), title, Empty);
}
