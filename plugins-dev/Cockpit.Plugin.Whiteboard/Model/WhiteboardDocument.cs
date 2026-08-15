using System.Collections.ObjectModel;

namespace Cockpit.Plugin.Whiteboard.Model;

public sealed class WhiteboardDocument
{
    public ObservableCollection<WhiteboardObject> Objects { get; } = [];

    public void Add(WhiteboardObject item) => Objects.Add(item);

    public bool Remove(Guid id)
    {
        var item = Find(id);
        return item is not null && Objects.Remove(item);
    }

    public WhiteboardObject? Find(Guid id) => Objects.FirstOrDefault(o => o.Id == id);
}
