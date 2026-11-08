using Avalonia.Threading;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugins.Abstractions.CompanionTools;
using Cockpit.Plugins.Abstractions.UI;
using Material.Icons;

namespace Cockpit.Plugin.Depot.UI;

// The "Live note" item on the companion strip: its badge counts the people in the followed note, and someone joining it opens the pop-out.
internal sealed class DepotLiveNoteTool(ICockpitUiHost host, DepotNotesModel notes, Func<IReadOnlyList<DepotConnectionRegistration>> connections)
{
    public const string Id = "depot.live-note";
    internal const string AutoOpenKey = "autoOpen";
    internal const string NoteKey = "note";

    private ICompanionToolContext? _context;
    private int _seen;

    public CompanionToolRegistration Registration => new(Id, "Live note", context => new DepotLiveNoteView(host, notes, connections, context))
    {
        IconKind = MaterialIconKind.NoteTextOutline,
        Tooltip = "Live note",
        Attached = _Attach,
    };

    internal static string KeyOf(string connectionId, DepotLiveNote note) => $"{connectionId}|{note.Address}|{note.Path}";

    // The stored note while it is still live, else the first live one.
    internal static (string ConnectionId, DepotLiveNote Note)? Followed(DepotNotesModel notes, string? stored)
    {
        var all = notes.Snapshot().SelectMany(page => page.Notes.Select(note => (page.ConnectionId, note))).ToList();
        return all.Count == 0 ? null : all.FirstOrDefault(item => KeyOf(item.ConnectionId, item.note) == stored) is { note: not null } match ? match : all[0];
    }

    private void _Attach(ICompanionToolContext context)
    {
        _context = context;
        notes.Changed += _ => Dispatcher.UIThread.Post(_Update);
        _Update();
    }

    private void _Update()
    {
        if (_context is not { } context)
        {
            return;
        }

        var count = Followed(notes, context.Storage.Get<string>(NoteKey)) is { } followed ? followed.Note.People.Count : 0;
        context.Badge.Text = count == 0 ? null : count.ToString();
        if (count > _seen && context.Storage.Get<bool?>(AutoOpenKey) != false)
        {
            context.RequestPopOut();
        }

        _seen = count;
    }
}
