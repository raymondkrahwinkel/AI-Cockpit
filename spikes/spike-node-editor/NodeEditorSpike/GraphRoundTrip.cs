using NodeEditor.Model;
using NodeEditor.Mvvm;

namespace NodeEditorSpike;

/// <summary>
/// Saves the graph and loads it back. A workflow that cannot survive being written to disk and read again is not
/// a workflow, it is a drawing — so this, not the look of the canvas, is what decides whether the library can
/// carry #69.
/// </summary>
internal static class GraphRoundTrip
{
    public static string Describe(IDrawingNode drawing)
    {
        var serializer = new NodeSerializer(typeof(System.Collections.ObjectModel.ObservableCollection<>));

        var json = serializer.Serialize(drawing);
        var loaded = serializer.Deserialize<DrawingNodeViewModel>(json);

        var nodes = loaded?.Nodes?.Count ?? 0;
        var connectors = loaded?.Connectors?.Count ?? 0;

        return $"round-trip: {json.Length} chars · loaded {nodes} nodes, {connectors} connectors";
    }
}
