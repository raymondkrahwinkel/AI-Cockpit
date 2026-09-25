namespace Cockpit.Plugin.Diagram.Collab;

// What ActivityStrip needs from whatever registry backs a surface: its journal, the change event, and undo
// (AC-870). Replaces the `bool whiteboard` it used to branch on — a third surface (AC-864) supplies its own
// implementation instead of the class growing a second flag.
internal interface ISurfaceActivityJournal
{
    event Action<string>? HistoryChanged;

    IReadOnlyList<SurfaceActivityEntry> History(string surfaceId);

    string? Revert(string surfaceId, string entryId);
}
