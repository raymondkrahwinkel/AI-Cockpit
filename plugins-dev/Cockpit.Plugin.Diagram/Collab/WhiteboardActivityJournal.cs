using Cockpit.Core.Abstractions.Whiteboard;

namespace Cockpit.Plugin.Diagram.Collab;

// Adapts IWhiteboardAccessRegistry to ISurfaceActivityJournal (AC-870). Only a Place entry can still be undone —
// see WhiteboardHistoryKind's own documented gap on Erase — so CanRevert mirrors the registry's own Revert rule
// rather than always allowing the button and letting the registry refuse it.
internal sealed class WhiteboardActivityJournal(IWhiteboardAccessRegistry? registry) : ISurfaceActivityJournal
{
    public event Action<string>? HistoryChanged
    {
        add
        {
            if (registry is not null)
            {
                registry.HistoryChanged += value;
            }
        }
        remove
        {
            if (registry is not null)
            {
                registry.HistoryChanged -= value;
            }
        }
    }

    public IReadOnlyList<SurfaceActivityEntry> History(string surfaceId) =>
        (registry?.History(surfaceId) ?? [])
            .Select(entry => new SurfaceActivityEntry(entry.Id, entry.Origin, entry.Summary, entry.ObjectId, entry.When, entry.Reverted, CanRevert: entry.Kind == WhiteboardHistoryKind.Place))
            .ToList();

    public string? Revert(string surfaceId, string entryId) => registry?.Revert(surfaceId, entryId);
}
