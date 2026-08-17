using Cockpit.Core.Abstractions.Wireframe;

namespace Cockpit.Plugin.Diagram.Collab;

// Adapts IWireframeAccessRegistry to ISurfaceActivityJournal and ISurfaceCouplingSource (AC-870/AC-873), the third
// alongside DiagramActivityJournal and WhiteboardActivityJournal. Null when an older host has no registry to
// resolve — same "no journal/coupling at all" state the other two leave ActivityStrip/PresenceIndicators in.
internal sealed class WireframeActivityJournal(IWireframeAccessRegistry? registry) : ISurfaceActivityJournal, ISurfaceCouplingSource
{
    private readonly Dictionary<Action<string, bool, bool>, Action<WireframeCouplingChange>> _couplingHandlers = new();

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

            void Forward(WireframeCouplingChange change) => value(change.SurfaceId, change.Coupling is not null, change.Coupling?.CanRead ?? false);
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

    // Every wireframe edit can be taken back (no WhiteboardHistoryKind.Erase-style gap on this surface, AC-871),
    // so CanRevert is always true — same as DiagramActivityJournal's.
    public IReadOnlyList<SurfaceActivityEntry> History(string surfaceId) =>
        (registry?.History(surfaceId) ?? [])
            .Select(entry => new SurfaceActivityEntry(entry.Id, entry.Origin, entry.Summary, _ObjectKey(entry.ComponentKey), entry.When, entry.Reverted, CanRevert: true))
            .ToList();

    public string? Revert(string surfaceId, string entryId) => registry?.Revert(surfaceId, entryId);

    // A Replace (WriteCoupled) journals an empty component key — there is no single line to jump to for a
    // whole-source rewrite, so the strip's row renders as unclickable rather than jumping nowhere.
    private static string? _ObjectKey(string componentKey) => string.IsNullOrEmpty(componentKey) ? null : componentKey;
}
