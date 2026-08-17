using Cockpit.Core.Abstractions.Whiteboard;

namespace Cockpit.Plugin.Diagram.Collab;

// Adapts IWhiteboardAccessRegistry to ISurfaceActivityJournal (AC-870) and ISurfaceCouplingSource (AC-879). Only a
// Place entry can still be undone — see WhiteboardHistoryKind's own documented gap on Erase — so CanRevert mirrors
// the registry's own Revert rule rather than always allowing the button and letting the registry refuse it.
internal sealed class WhiteboardActivityJournal(IWhiteboardAccessRegistry? registry) : ISurfaceActivityJournal, ISurfaceCouplingSource
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
        }
        remove
        {
            if (registry is not null)
            {
                registry.HistoryChanged -= value;
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
        (registry?.History(surfaceId) ?? [])
            .Select(entry => new SurfaceActivityEntry(entry.Id, entry.Origin, entry.Summary, entry.ObjectId, entry.When, entry.Reverted, CanRevert: entry.Kind == WhiteboardHistoryKind.Place))
            .ToList();

    public string? Revert(string surfaceId, string entryId) => registry?.Revert(surfaceId, entryId);
}
