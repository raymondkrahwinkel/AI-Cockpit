namespace Cockpit.Plugin.Diagram;

// The diagram a window stands for (AC-834). `Id` is what the window keys on — one window per document, never per
// session — so it has to be the diagram's own stable identity: AC-812's file path for a saved one, a fresh id for
// a diagram that has no file yet. It doubles as the surface id the AC-810 registry knows this diagram by.
internal sealed record DiagramDocument(string Id, string Title, string MermaidText)
{
    public const string Sample = """
        flowchart LR
            Zip[Plugin zip] -->|PluginLoadContext| Fallthrough{Falls through?}
            Fallthrough -->|Avalonia, Skia| Host[Host's own copy]
            Fallthrough -->|Mermaider| Own[Plugin's own copy]
            Host --> Panel[This panel]
            Own --> Panel
        """;

    public static DiagramDocument New(string title) => new(Guid.NewGuid().ToString("n"), title, Sample);
}
