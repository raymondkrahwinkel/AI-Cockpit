using Cockpit.Core.Abstractions.Diagrams;

namespace Cockpit.Plugin.Diagram.Collab;

// Adapts IDiagramAccessRegistry to ISurfaceActivityJournal (AC-870). Null when an older host has no registry to
// resolve — same "no journal at all" state the registry-less branch left ActivityStrip in before this ticket.
internal sealed class DiagramActivityJournal(IDiagramAccessRegistry? registry) : ISurfaceActivityJournal
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
            .Select(entry => new SurfaceActivityEntry(entry.Id, entry.Origin, entry.Summary, entry.ObjectKey, entry.When, entry.Reverted, CanRevert: true))
            .ToList();

    public string? Revert(string surfaceId, string entryId) => registry?.Revert(surfaceId, entryId);
}
