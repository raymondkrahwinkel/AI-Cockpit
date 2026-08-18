using Cockpit.Core.Abstractions.Whiteboard;
using Cockpit.Plugin.Diagram.Whiteboard.Model;

namespace Cockpit.Plugin.Diagram.Collab;

// Adapts IWhiteboardAccessRegistry to ISurfaceActivityJournal (AC-870) and ISurfaceCouplingSource (AC-879), merged
// with the operator's own handlings (AC-912) so one strip shows both halves of the collaboration. Only a Place entry
// can still be undone on the agent side — see WhiteboardHistoryKind's documented gap on Erase.
internal sealed class WhiteboardActivityJournal(IWhiteboardAccessRegistry? registry, WhiteboardEditJournal? edits = null)
    : ISurfaceActivityJournal, ISurfaceCouplingSource
{
    // Same reason as DiagramActivityJournal's own map: CouplingChanged's flattened signature differs from the
    // registry's own event, so remove needs the exact wrapper add registered rather than a straight pass-through.
    private readonly Dictionary<Action<string, bool, bool>, Action<WhiteboardCouplingChange>> _couplingHandlers = new();

    public event Action<string>? HistoryChanged
    {
        add
        {
            if (registry is not null)
            {
                registry.HistoryChanged += value;
            }

            if (edits is not null)
            {
                edits.Changed += value;
            }
        }
        remove
        {
            if (registry is not null)
            {
                registry.HistoryChanged -= value;
            }

            if (edits is not null)
            {
                edits.Changed -= value;
            }
        }
    }

    public event Action<string, bool, bool>? CouplingChanged
    {
        add
        {
            if (registry is null || value is null)
            {
                return;
            }

            void Forward(WhiteboardCouplingChange change) => value(change.SurfaceId, change.Coupling is not null, change.Coupling?.CanRead ?? false);
            _couplingHandlers[value] = Forward;
            registry.CouplingChanged += Forward;
        }
        remove
        {
            if (registry is null || value is null || !_couplingHandlers.Remove(value, out var forward))
            {
                return;
            }

            registry.CouplingChanged -= forward;
        }
    }

    public IReadOnlyList<SurfaceActivityEntry> History(string surfaceId) =>
        [.. (registry?.History(surfaceId) ?? [])
            .Select(entry => new SurfaceActivityEntry(entry.Id, entry.Origin, entry.Summary, entry.ObjectId, entry.When, entry.Reverted, CanRevert: entry.Kind == WhiteboardHistoryKind.Place))
            .Concat((edits?.Entries ?? []).Select(entry => new SurfaceActivityEntry(entry.Id, "operator", entry.Summary, entry.ObjectId.ToString(), entry.When, entry.Reverted, CanRevert: true)))
            .OrderBy(entry => entry.When)];

    // An operator row's inverse lives in the plugin (it works on WhiteboardDocument), the agent's in the registry;
    // the entry id says which, so the strip needs no second button and no journal of its own.
    public string? Revert(string surfaceId, string entryId) =>
        edits is not null && edits.Entries.Any(entry => entry.Id == entryId)
            ? edits.Undo(entryId)
            : registry?.Revert(surfaceId, entryId);
}
