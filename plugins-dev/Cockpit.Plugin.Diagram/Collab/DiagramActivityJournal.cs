using Cockpit.Core.Abstractions.Diagrams;

namespace Cockpit.Plugin.Diagram.Collab;

// Adapts IDiagramAccessRegistry to ISurfaceActivityJournal (AC-870) and ISurfaceCouplingSource (AC-879). Null when
// an older host has no registry to resolve — same "no journal/coupling at all" state the registry-less branch left
// ActivityStrip/PresenceIndicators in before these tickets.
internal sealed class DiagramActivityJournal(IDiagramAccessRegistry? registry) : ISurfaceActivityJournal, ISurfaceCouplingSource
{
    // Maps each subscriber's own delegate to the registry-shaped wrapper actually registered on the registry, so
    // remove detaches the exact same handler add attached — CouplingChanged's flattened signature differs from the
    // registry's own event, unlike HistoryChanged below, so a straight pass-through will not do.
    private readonly Dictionary<Action<string, bool, bool>, Action<DiagramCouplingChange>> _couplingHandlers = new();

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

            void Forward(DiagramCouplingChange change) => value(change.SurfaceId, change.Coupling is not null, change.Coupling?.CanRead ?? false);
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
            .Select(entry => new SurfaceActivityEntry(entry.Id, entry.Origin, entry.Summary, entry.ObjectKey, entry.When, entry.Reverted, CanRevert: true))
            .ToList();

    public string? Revert(string surfaceId, string entryId) => registry?.Revert(surfaceId, entryId);
}
