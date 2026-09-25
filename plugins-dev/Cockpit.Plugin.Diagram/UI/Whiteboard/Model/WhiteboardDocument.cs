using System.Collections.ObjectModel;

namespace Cockpit.Plugin.Diagram.Whiteboard.Model;

// Id/Title/FilePath mirror DiagramDocument (AC-834): Id is the window/surface identity — a fresh guid for an
// unsaved board, the file path once it has one (W-2/AC-843) — Title is what "heropenen" shows everywhere.
public sealed class WhiteboardDocument(string? id = null, string title = "Whiteboard", string? filePath = null)
{
    public string Id { get; } = id ?? Guid.NewGuid().ToString("n");

    public string Title { get; set; } = title;

    public string? FilePath { get; set; } = filePath;

    public ObservableCollection<WhiteboardObject> Objects { get; } = [];

    public void Add(WhiteboardObject item) => Objects.Add(item);

    public bool Remove(Guid id)
    {
        var item = Find(id);
        return item is not null && Objects.Remove(item);
    }

    public WhiteboardObject? Find(Guid id) => Objects.FirstOrDefault(o => o.Id == id);
}
